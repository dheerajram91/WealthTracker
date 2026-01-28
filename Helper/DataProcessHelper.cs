using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace WealthTracker.Helper
{
    public class DataProcessHelper
    {
        private static string _csvPath = string.Empty;
        private static string _baseUrl = string.Empty;
        private readonly IMemoryCache _memoryCache;
        private readonly ConcurrentDictionary<string, DateTime> _oldestDataCache = new ConcurrentDictionary<string, DateTime>();

        public DataProcessHelper(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        }

        public static DataProcessHelper Instance = null!;

        public static void Initialize(string csvPath, string baseUrl)
        {
            _csvPath = csvPath;
            _baseUrl = baseUrl;
        }

        public async Task<DateTime> GetOldestDateAsync(string symbol)
        {
            if (!string.IsNullOrEmpty(symbol) && _oldestDataCache.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            var prices = await GetOrLoadPriceDataAsync(symbol).ConfigureAwait(false);
            if (prices == null || !prices.Any())
            {
                // No data available — return MinValue so callers can handle absence safely
                return DateTime.MinValue;
            }

            var oldest = prices.Keys.Min();
            _oldestDataCache[symbol] = oldest;
            return oldest;
        }

        public async Task<Dictionary<DateTime, double>> GetOrLoadPriceDataAsync(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return new Dictionary<DateTime, double>();

            var cacheKey = $"PriceData_{symbol.ToUpperInvariant()}";

#pragma warning disable CS8603 // Possible null reference return.
            return await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
            {
                // Tune expiration as needed; absolute expiration here prevents stale long-term caching
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);

                var csvPrices = LoadPricesFromCsv(symbol) ?? new Dictionary<DateTime, double>();
                var combined = new Dictionary<DateTime, double>(csvPrices);

                // Determine start date for refresh: one day after the last date present in CSV (if any)
                DateTime refreshStart;
                if (csvPrices.Any())
                {
                    refreshStart = csvPrices.Keys.Max().AddDays(1);
                }
                else
                {
                    refreshStart = DateTime.Today;
                }

                var refreshEnd = DateTime.Today;

                if (!string.Equals(symbol, "CASH", StringComparison.OrdinalIgnoreCase))
                {
                    // Call AlphaVantage endpoint for additional dates only when symbol is NOT CASH
                    // Only call the API if there's a valid range to request
                    if (refreshStart <= refreshEnd)
                    {
                        var apiPrices = await LoadPricesFromAlphaVantageApi(symbol, refreshStart, refreshEnd).ConfigureAwait(false);
                        if (apiPrices != null)
                        {
                            // Merge API prices, but prefer CSV values for overlapping dates
                            foreach (var kvp in apiPrices)
                            {
                                if (!combined.ContainsKey(kvp.Key))
                                    combined[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
                else
                {
                    // CASH: fill with fixed value 100.0 for all dates starting one day after CSV last date through today
                    var start = refreshStart;
                    var end = refreshEnd;
                    if (start <= end)
                    {
                        for (var d = start; d <= end; d = d.AddDays(1))
                        {
                            if (!combined.ContainsKey(d))
                                combined[d] = 100.0;
                        }
                    }
                }

                // Ensure data is sorted by date
                return combined.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }).ConfigureAwait(false);
#pragma warning restore CS8603 // Possible null reference return.
        }

        private Dictionary<DateTime, double> LoadPricesFromCsv(string symbol)
        {
            var prices = new Dictionary<DateTime, double>();
            var filePath = Path.Combine(_csvPath, $"{symbol}.csv");

            if (!System.IO.File.Exists(filePath)) return prices;

            using (var reader = new StreamReader(filePath))
            {
                // Skip header
                reader.ReadLine();
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Handle quoted dates like "Dec 27, 2025"
                    var parts = line.Split(new[] { "\",", "," }, StringSplitOptions.None);
                    string dateStr_1 = parts[0].Trim('"');
                    string dateStr_2 = parts[1];
                    string dateStr = dateStr_1 + "," + dateStr_2;
                    if (DateTime.TryParseExact(dateStr, "MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    {
                        // parts[5] is the 'Close' price
                        if (double.TryParse(parts[5], out double closePrice))
                        {
                            prices[date] = closePrice;
                        }
                    }
                }
            }
            // Return sorted by date
            return prices.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        private async Task<Dictionary<DateTime, double>> LoadPricesFromAlphaVantageApi(string symbol, DateTime start, DateTime end)
        {
            var result = new Dictionary<DateTime, double>();
            try
            {
                // Build local absolute URL to the StockController endpoint
                var baseUrl = _baseUrl;
                var url = $"{baseUrl}/stock?symbol={Uri.EscapeDataString(symbol)}&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";

                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var resp = await client.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return result;

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var dataElement)) return result;
                foreach (var item in dataElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("date", out var dateProp)) continue;
                    if (!item.TryGetProperty("close", out var closeProp)) continue;

                    var dateStr = dateProp.GetString();
                    if (string.IsNullOrWhiteSpace(dateStr)) continue;
                    if (!DateTime.TryParse(dateStr, out var date)) continue;

                    double closeValue = 0;
                    if (closeProp.ValueKind == JsonValueKind.Number && closeProp.TryGetDouble(out var d))
                    {
                        closeValue = d;
                    }
                    else if (closeProp.ValueKind == JsonValueKind.String && double.TryParse(closeProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    {
                        closeValue = parsed;
                    }

                    result[date] = closeValue;
                }
            }
            catch
            {
                // swallow and return empty result for fallback behavior
            }

            return result;
        }

        public double GetPriceOnOrBefore(DateTime date, Dictionary<DateTime, double> priceData)
        {
            // If exact date exists, return it
            if (priceData.TryGetValue(date, out double price)) return price;

            // Otherwise, find the latest price before this date (handling weekends/holidays)
            var previousDates = priceData.Keys.Where(d => d < date);
            if (!previousDates.Any()) return priceData.First().Value;

            return priceData[previousDates.Max()];
        }

        public List<DateTime> GetMonthlyDates(DateTime start, DateTime end)
        {
            var dates = new List<DateTime>();
            // Start at the first month-start on or after start date
            DateTime current = new DateTime(start.Year, start.Month, 1);
            if (current < start) current = current.AddMonths(1);

            while (current <= end)
            {
                dates.Add(current);
                current = current.AddMonths(1);
            }
            return dates;
        }
    }
}

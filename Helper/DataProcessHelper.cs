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

        private static readonly Dictionary<string, string> _symbolDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTYBEES"] = "Nifty 50 ETF (India)",
            ["SENSEXBEES"] = "Sensex ETF (India)",
            ["FXI"] = "iShares China Large-Cap ETF",
            ["BTC"] = "Bitcoin (crypto)",
            ["ETH"] = "Ethereum (crypto)",
            ["QQQ"] = "Invesco QQQ (Nasdaq-100 ETF)",
            ["IXN"] = "iShares Global Tech ETF",
            ["IAU"] = "iShares Gold Trust",
            ["SLV"] = "iShares Silver Trust",
            ["DIA"] = "SPDR Dow Jones Industrial Average ETF",
            ["VOO"] = "Vanguard S&P 500 ETF",
            ["CASH"] = "Cash / Stable"
        };

        public static Dictionary<string, string> SymbolDescriptions => _symbolDescriptions;
        public static List<string> AllowedSymbols => _symbolDescriptions.Keys.ToList();

        public DataProcessHelper(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        }

        public static DataProcessHelper Instance = null!;

        public static void Initialize(string csvPath, string baseUrl)
        {
            _csvPath = csvPath;
            if (!string.IsNullOrEmpty(baseUrl))
            {
                _baseUrl = baseUrl;
            }
            LoadSymbolDescriptions();
        }

        private static void LoadSymbolDescriptions()
        {
            if (string.IsNullOrEmpty(_csvPath)) return;

            var jsonPath = Path.Combine(_csvPath, "symbols.json");
            if (System.IO.File.Exists(jsonPath))
            {
                try
                {
                    var json = System.IO.File.ReadAllText(jsonPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        _symbolDescriptions.Clear();
                        foreach (var kvp in dict)
                        {
                            _symbolDescriptions[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch
                {
                    // Fallback to default
                }
            }

            // Supplement with any CSVs found in the directory that aren't in the descriptions map
            if (System.IO.Directory.Exists(_csvPath))
            {
                var files = System.IO.Directory.GetFiles(_csvPath, "*.csv");
                foreach (var file in files)
                {
                    var sym = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                    if (!_symbolDescriptions.ContainsKey(sym))
                    {
                        _symbolDescriptions[sym] = $"{sym} Asset";
                    }
                }
            }
        }

        public async Task<DateTime> GetOldestDateAsync(string symbol)
        {
            if (string.IsNullOrEmpty(symbol)) return DateTime.MinValue;

            if (_oldestDataCache.TryGetValue(symbol, out var cached))
            {
                return cached;
            }

            var filePath = Path.Combine(_csvPath, $"{symbol}.csv");
            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    var lines = System.IO.File.ReadAllLines(filePath);
                    if (lines.Length > 1)
                    {
                        var firstLine = lines[1];
                        var lastLine = lines[lines.Length - 1];

                        DateTime? firstDate = ParseDateFromLine(firstLine);
                        DateTime? lastDate = ParseDateFromLine(lastLine);

                        if (firstDate.HasValue && lastDate.HasValue)
                        {
                            var resultOldest = firstDate.Value < lastDate.Value ? firstDate.Value : lastDate.Value;
                            _oldestDataCache[symbol] = resultOldest;
                            return resultOldest;
                        }
                        else if (firstDate.HasValue)
                        {
                            _oldestDataCache[symbol] = firstDate.Value;
                            return firstDate.Value;
                        }
                    }
                }
                catch
                {
                    // Fallback to loading the entire file if reading fails
                }
            }

            // Fallback parsing (e.g. if CSV structure is different or read failed)
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

        private static DateTime? ParseDateFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            // Try 2-column yyyy-MM-dd
            var parts = line.Split(',');
            if (parts.Length == 2)
            {
                if (DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                    return d;
            }

            // Try Yahoo format "Dec 19, 2025"
            var yahooParts = line.Split(new[] { "\",", "," }, StringSplitOptions.None);
            if (yahooParts.Length >= 2)
            {
                string dateStr1 = yahooParts[0].Trim('"');
                string dateStr2 = yahooParts[1].Trim('"');
                string dateStr = $"{dateStr1},{dateStr2}";
                if (DateTime.TryParseExact(dateStr.Trim(), new[] { "MMM dd, yyyy", "MMM d, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                    return d;
            }

            // Try 7-column yyyy-MM-dd
            if (parts.Length >= 1)
            {
                if (DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                    return d;
            }

            return null;
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

                    // Try parsing the 2-column yyyy-MM-dd format first
                    var parts = line.Split(',');
                    if (parts.Length == 2)
                    {
                        string dateStr = parts[0].Trim();
                        string priceStr = parts[1].Trim();
                        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) &&
                            double.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double closePrice))
                        {
                            prices[date] = closePrice;
                            continue;
                        }
                    }

                    // Try parsing the 7-column Yahoo format: "Dec 19, 2025",Open,High,Low,Close,Adj Close,Volume
                    var yahooParts = line.Split(new[] { "\",", "," }, StringSplitOptions.None);
                    if (yahooParts.Length >= 6)
                    {
                        string dateStr1 = yahooParts[0].Trim('"');
                        string dateStr2 = yahooParts[1].Trim('"');
                        string dateStr = $"{dateStr1},{dateStr2}";
                        if (DateTime.TryParseExact(dateStr.Trim(), new[] { "MMM dd, yyyy", "MMM d, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) &&
                            double.TryParse(yahooParts[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double closePrice))
                        {
                            prices[date] = closePrice;
                            continue;
                        }
                    }

                    // Try 7-column standard format with yyyy-MM-dd date
                    if (parts.Length >= 6)
                    {
                        string dateStr = parts[0].Trim();
                        if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) &&
                            double.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double closePrice))
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

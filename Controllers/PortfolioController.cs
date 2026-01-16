using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WealthTracker.Models;

namespace WealthTracker.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PortfolioController : ControllerBase
    {
        private readonly string _csvPath = Path.Combine(Directory.GetCurrentDirectory(), "Data"); // Path where CSVs are stored
        private readonly DateTime _minDate = new DateTime(1990, 01, 01);
        private readonly DateTime _maxDate = DateTime.Today;
        private readonly IMemoryCache _memoryCache;
        private readonly ConcurrentDictionary<string, DateTime> _oldestDataCache = new ConcurrentDictionary<string, DateTime>();

        public PortfolioController(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        }

        [HttpGet("decode")]
        public IActionResult Decode([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code)) return BadRequest("Code is required.");

            var request = DecodeRequest(code); // Uses the helper method provided in the previous turn
            if (request == null) return BadRequest("Invalid share code.");

            return Ok(request);
        }

        [HttpPost]
        public async Task<IActionResult> CalculatePortfolio([FromBody] PortfolioRequest request)
        {
            // 1. Validation
            if (request.Periods == null || !request.Periods.Any())
                return BadRequest("Periods are required.");

            var sortedPeriods = request.Periods.OrderBy(p => p.StartDate).ToList();

            for (int i = 0; i < sortedPeriods.Count; i++)
            {
                var p = sortedPeriods[i];

                DateTime currentLowestStartTime = DateTime.MinValue;
                string symbol = string.Empty;
                foreach (var key in p.Items.Keys.ToList())
                {
                    var currentOldDate = await GetOldestDateAsync(key).ConfigureAwait(false);
                    if (currentOldDate > currentLowestStartTime)
                    {
                        symbol = key;
                        currentLowestStartTime = currentOldDate;
                    }
                }

                // Check Date Range
                if (p.StartDate < _minDate || p.EndDate > _maxDate)
                    return BadRequest($"Dates must be between {_minDate:yyyy-MM-dd} and {_maxDate:yyyy-MM-dd}.");

                if (p.StartDate < currentLowestStartTime)
                {
                    return BadRequest($"Oldest data available for {symbol} is {currentLowestStartTime}");
                }

                // Check Percentage Sum
                if (Math.Abs(p.Items.Values.Sum() - 100.0) > 0.01)
                    return BadRequest($"Items in period starting {p.StartDate:yyyy-MM-dd} must sum to 100%.");

                // Check Continuity
                if (i > 0 && (p.StartDate - sortedPeriods[i - 1].EndDate).TotalDays != 1)
                    return BadRequest($"Periods must be consecutive. {p.StartDate:yyyy-MM-dd} does not follow {sortedPeriods[i - 1].EndDate:yyyy-MM-dd}.");
            }

            // 2. Load and Process Data
            // Optimization: Load only necessary CSVs into memory and cache results to reuse across API calls
            var symbols = request.Periods.SelectMany(p => p.Items.Keys).Distinct();
            var priceData = new Dictionary<string, Dictionary<DateTime, double>>();

            foreach (var sym in symbols)
            {
                // Use cached/computed price data; GetOrLoadPriceDataAsync will load CSV + API once and cache result
                var combined = await GetOrLoadPriceDataAsync(sym).ConfigureAwait(false);
                priceData[sym] = combined;
            }

            // 3. Calculation Logic
            double a = 1.0; // Cumulative growth of X
            double b = 0.0; // Cumulative growth of Y additions

            var startOfSeries = sortedPeriods.First().StartDate;
            var endOfSeries = sortedPeriods.Last().EndDate;

            // Define Monthly Investment Dates (1st of every month within range)
            var monthlyDates = GetMonthlyDates(startOfSeries, endOfSeries);

            // Calculate Wealth Growth Monthly (X=100, Y=5 for graph1 and X=100, Y=0 for graph2)
            var graphData = new List<MonthlyWealthPoint>();
            var graphDataNoSIP = new List<MonthlyWealthPoint>();

            double currentXGrowth = 1.0;
            var yInvestments = new List<(DateTime Date, double Multiplier)>();

            foreach (var date in monthlyDates)
            {
                // Update growth factor for X and existing Ys for the month
                var period = sortedPeriods.FirstOrDefault(p => date >= p.StartDate && date <= p.EndDate);
                if (period == null) continue;

                // Simple Monthly Portfolio Return: Sum(weight_i * (Price_end_of_month / Price_start_of_month))
                // For simplicity in this logic, we calculate daily/monthly growth
                double monthlyReturn = CalculatePortfolioReturn(date, date.AddMonths(1).AddDays(-1), period.Items, priceData);
            }

            // Simplified Result based on simulation:
            // Coefficient A: Final Value of 1 unit invested at start
            // Coefficient B: Sum of final values of 1 unit invested every month

            // Perform final simulation to get a and b
            (a, b, graphData, graphDataNoSIP) = RunSimulation(sortedPeriods, priceData, 100, 5);

            return Ok(new PortfolioResponse
            {
                CoefficientA = Math.Round(a, 4),
                CoefficientB = Math.Round(b, 4),
                Polynomial = $"{a:F2}X + {b:F2}Y",
                GraphData = graphData,
                GraphDataNoSIP = graphDataNoSIP,
                ShareCode = EncodeRequest(request)
            });
        }

        // Cache-aware loader: reads CSV, determines last CSV date (if any), requests only new dates from API, then caches the sorted dictionary
        private async Task<Dictionary<DateTime, double>> GetOrLoadPriceDataAsync(string symbol)
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
                    // If CSV has no data, don't request a huge historical range.
                    // Use _minDate to allow full history, or choose DateTime.Today to only check today's price.
                    // Here we default to _minDate to preserve previous behavior if desired.
                    refreshStart = _minDate;
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
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
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

        private double GetPriceOnOrBefore(DateTime date, Dictionary<DateTime, double> priceData)
        {
            // If exact date exists, return it
            if (priceData.TryGetValue(date, out double price)) return price;

            // Otherwise, find the latest price before this date (handling weekends/holidays)
            var previousDates = priceData.Keys.Where(d => d < date);
            if (!previousDates.Any()) return priceData.First().Value;

            return priceData[previousDates.Max()];
        }

        private List<DateTime> GetMonthlyDates(DateTime start, DateTime end)
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

        private (double a, double b, List<MonthlyWealthPoint> graphData, List<MonthlyWealthPoint> graphDataNoSIP) RunSimulation(
            List<PortfolioPeriod> periods,
            Dictionary<string, Dictionary<DateTime, double>> allPriceData,
            double graphX,
            double graphY)
        {
            var startOfSeries = periods.First().StartDate;
            var endOfSeries = periods.Last().EndDate;
            var monthlyDates = GetMonthlyDates(startOfSeries, endOfSeries);

            // Track three variables:
            double coeffA = 1.0; // Growth of the initial $1
            double coeffB = 0.0; // Growth of the monthly $1 investments
            var graphPoints = new List<MonthlyWealthPoint>();
            var graphPoints_no_SIP = new List<MonthlyWealthPoint>();

            DateTime lastCheckDate = startOfSeries;

            foreach (var currentDate in monthlyDates)
            {
                // 1. Calculate return from lastCheckDate to currentDate
                double periodReturn = GetPortfolioReturn(lastCheckDate, currentDate, periods, allPriceData);

                // 2. Grow existing values
                coeffA *= periodReturn;
                coeffB *= periodReturn;

                // 3. Inject new monthly investment for coeffB (the $1)
                coeffB += 1.0;

                // 4. Calculate current wealth for the graph (X=100, Y=5)
                double currentWealth = (graphX * coeffA) + (graphY * coeffB);
                graphPoints.Add(new MonthlyWealthPoint
                {
                    Month = currentDate.ToString("MMM yyyy"),
                    Wealth = Math.Round(currentWealth, 2)
                });

                // 4. Calculate current wealth for the graph (X=100, Y=0)
                currentWealth = (graphX * coeffA);
                graphPoints_no_SIP.Add(new MonthlyWealthPoint
                {
                    Month = currentDate.ToString("MMM yyyy"),
                    Wealth = Math.Round(currentWealth, 2)
                });
                lastCheckDate = currentDate;
            }

            // Finally, grow from the last monthly date to the absolute EndDate
            double finalReturn = GetPortfolioReturn(lastCheckDate, endOfSeries, periods, allPriceData);
            coeffA *= finalReturn;
            coeffB *= finalReturn;

            return (coeffA, coeffB, graphPoints, graphPoints_no_SIP);
        }

        private double GetPortfolioReturn(
            DateTime start,
            DateTime end,
            List<PortfolioPeriod> periods,
            Dictionary<string, Dictionary<DateTime, double>> allPriceData)
        {
            // Find the weight definition for this date range
            // (Simplification: Use the period valid at the 'start' date)
            var period = periods.FirstOrDefault(p => start >= p.StartDate && start <= p.EndDate)
                         ?? periods.First();

            double totalReturn = 0;

            foreach (var item in period.Items)
            {
                string symbol = item.Key;
                double weight = item.Value / 100.0;

                if (allPriceData.ContainsKey(symbol))
                {
                    double priceStart = GetPriceOnOrBefore(start, allPriceData[symbol]);
                    double priceEnd = GetPriceOnOrBefore(end, allPriceData[symbol]);

                    double assetReturn = priceEnd / priceStart;
                    totalReturn += (assetReturn * weight);
                }
            }

            return totalReturn;
        }

        /// <summary>
        /// Calculates the weighted growth factor for the portfolio between two dates.
        /// A result of 1.05 means a 5% gain.
        /// </summary>
        private double CalculatePortfolioReturn(
            DateTime startDate,
            DateTime endDate,
            Dictionary<string, double> weights,
            Dictionary<string, Dictionary<DateTime, double>> priceData)
        {
            double totalPortfolioGrowthFactor = 0;

            foreach (var item in weights)
            {
                string symbol = item.Key;
                double weightPercentage = item.Value / 100.0; // Convert 20% to 0.2

                if (priceData.TryGetValue(symbol, out var individualAssetPrices))
                {
                    // Get prices using the 'OnOrBefore' helper to handle weekends/holidays
                    double priceAtStart = GetPriceOnOrBefore(startDate, individualAssetPrices);
                    double priceAtEnd = GetPriceOnOrBefore(endDate, individualAssetPrices);

                    // growth = PriceEnd / PriceStart
                    double assetGrowthFactor = priceAtEnd / priceAtStart;

                    // Add weighted contribution to the portfolio
                    totalPortfolioGrowthFactor += (assetGrowthFactor * weightPercentage);
                }
                else
                {
                    // If data for a symbol is missing, assume no growth (1.0) 
                    // or handle as an error based on your requirements.
                    totalPortfolioGrowthFactor += (1.0 * weightPercentage);
                }
            }

            return totalPortfolioGrowthFactor;
        }

        // Converts the input object to a unique alphanumeric string
        private string EncodeRequest(PortfolioRequest request)
        {
            // 1. Serialize to JSON
            string json = JsonSerializer.Serialize(request);

            // 2. Convert to bytes
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);

            // 3. Base64Url encode (removes +, / and = to make it URL safe)
            return WebEncoders.Base64UrlEncode(bytes);
        }

        // Decodes the string back into a PortfolioRequest object
        private PortfolioRequest DecodeRequest(string code)
        {
            try
            {
                byte[] bytes = WebEncoders.Base64UrlDecode(code);
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<PortfolioRequest>(json);
            }
            catch
            {
                return null;
            }
        }

        private async Task<DateTime> GetOldestDateAsync(string symbol)
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
    }
}

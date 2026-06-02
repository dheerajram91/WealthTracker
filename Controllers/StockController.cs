using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace WealthTracker.Controllers
{
    [ApiController]
    public class StockController : ControllerBase
    {
        private readonly ILogger<StockController> _logger;
        private readonly string _avApiKey = "BVJ2VKR5FAZCZQ3Q";

        public StockController(ILogger<StockController> logger)
        {
            _logger = logger;
        }

        [HttpGet("/stock")]
        public async Task<IActionResult> Get(
            [FromQuery] string? symbol,
            [FromQuery] string? market,
            [FromQuery] string? start_date,
            [FromQuery] string? end_date,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return BadRequest(new { error = "Missing symbol" });
            if (!DateTime.TryParse(start_date, out var startDate) || !DateTime.TryParse(end_date, out var endDate))
                return BadRequest(new { error = "Invalid date format" });

            // If NIFTYBEES or SENSEXBEES, go straight to Yahoo Finance since AlphaVantage does not support them on standard/free tiers
            bool isIndianEtf = symbol.Equals("NIFTYBEES", StringComparison.OrdinalIgnoreCase) ||
                              symbol.Equals("SENSEXBEES", StringComparison.OrdinalIgnoreCase);

            if (!isIndianEtf)
            {
                try
                {
                    var avData = await GetFromAlphaVantage(symbol, startDate, endDate, cancellationToken);
                    if (avData != null && avData.Any())
                    {
                        return Ok(new { source = "AlphaVantage", symbol = symbol.ToUpper(), data = avData });
                    }
                    _logger.LogInformation("Alpha Vantage returned no data for {Symbol}.", symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Alpha Vantage failed: {Msg}. ", ex.Message);
                }
            }

            // Fallback to Yahoo Finance
            try
            {
                _logger.LogInformation("Querying Yahoo Finance for {Symbol}...", symbol);
                var yfData = await GetFromYahooFinance(symbol, startDate, endDate, cancellationToken);
                if (yfData != null && yfData.Any())
                {
                    return Ok(new { source = "YahooFinance", symbol = symbol.ToUpper(), data = yfData });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Yahoo Finance fallback failed: {Msg}.", ex.Message);
            }

            return NotFound(new { error = "No data found in Alpha Vantage or Yahoo Finance" });
        }

        private async Task<List<object>?> GetFromAlphaVantage(string symbol, DateTime start, DateTime end, CancellationToken ct)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Always use outputsize=compact because outputsize=full is a premium feature on the free tier of AlphaVantage.
            var outputSize = "compact";

            bool isCrypto = false;
            if (symbol.Equals("BTC", StringComparison.OrdinalIgnoreCase) || 
               symbol.Equals("ETH", StringComparison.OrdinalIgnoreCase) ||
               symbol.Equals("LTC", StringComparison.OrdinalIgnoreCase))
            {
                isCrypto = true;
            }

            var apiSymbol = MapSymbolForAlphaVantage(symbol);
            var url = isCrypto
                ? $"https://www.alphavantage.co/query?function=DIGITAL_CURRENCY_DAILY&symbol={apiSymbol}&market=USD&apikey={_avApiKey}"
                : $"https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={apiSymbol}&outputsize={outputSize}&apikey={_avApiKey}";
            var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var rootProp = isCrypto ? "Time Series (Digital Currency Daily)" : "Time Series (Daily)";
            if (!doc.RootElement.TryGetProperty(rootProp, out var timeSeries)) return null;

            return timeSeries.EnumerateObject()
                .Select(d => new { Date = DateTime.Parse(d.Name), DateStr = d.Name, Close = d.Value.GetProperty("4. close").GetString() })
                .Where(x => x.Date >= start && x.Date <= end)
                .OrderBy(x => x.Date)
                .Select(x => (object)new { date = x.DateStr, close = decimal.Parse(x.Close ?? "0", CultureInfo.InvariantCulture) })
                .ToList();
        }

        private async Task<List<object>?> GetFromYahooFinance(string symbol, DateTime start, DateTime end, CancellationToken ct)
        {
            string yahooSymbol = symbol;
            if (symbol.Equals("NIFTYBEES", StringComparison.OrdinalIgnoreCase))
            {
                yahooSymbol = "NIFTYBEES.NS";
            }
            else if (symbol.Equals("SENSEXBEES", StringComparison.OrdinalIgnoreCase))
            {
                yahooSymbol = "SENSEXBEES.BO";
            }
            else if (symbol.Equals("BTC", StringComparison.OrdinalIgnoreCase))
            {
                yahooSymbol = "BTC-USD";
            }
            else if (symbol.Equals("ETH", StringComparison.OrdinalIgnoreCase))
            {
                yahooSymbol = "ETH-USD";
            }
            else if (symbol.Equals("LTC", StringComparison.OrdinalIgnoreCase))
            {
                yahooSymbol = "LTC-USD";
            }

            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{yahooSymbol}?interval=1d&range=1y";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var resp = await client.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                var chart = doc.RootElement.GetProperty("chart");
                var resultArr = chart.GetProperty("result");
                if (resultArr.ValueKind == JsonValueKind.Array && resultArr.GetArrayLength() > 0)
                {
                    var root = resultArr[0];
                    if (root.TryGetProperty("timestamp", out var timestamps) &&
                        root.TryGetProperty("indicators", out var indicators) &&
                        indicators.TryGetProperty("quote", out var quoteArr) &&
                        quoteArr.ValueKind == JsonValueKind.Array && quoteArr.GetArrayLength() > 0)
                    {
                        var quote = quoteArr[0];
                        if (quote.TryGetProperty("close", out var closes))
                        {
                            var list = new List<object>();
                            int count = timestamps.GetArrayLength();
                            for (int i = 0; i < count; i++)
                            {
                                if (closes[i].ValueKind == JsonValueKind.Null) continue;

                                long unixSec = timestamps[i].GetInt64();
                                DateTime date = DateTimeOffset.FromUnixTimeSeconds(unixSec).LocalDateTime.Date;

                                if (date >= start && date <= end)
                                {
                                    double price = 0.0;
                                    if (closes[i].ValueKind == JsonValueKind.Number && closes[i].TryGetDouble(out var closeVal))
                                    {
                                        price = closeVal;
                                    }
                                    list.Add((object)new { date = date.ToString("yyyy-MM-dd"), close = price });
                                }
                            }
                            return list;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Yahoo Finance fallback failed for {Symbol}: {Msg}", symbol, ex.Message);
            }

            return null;
        }

        private static string MapSymbolForAlphaVantage(string symbol)
        {
            if (symbol.Equals("NIFTYBEES", StringComparison.OrdinalIgnoreCase))
            {
                return "NIFTYBEES.NS";
            }
            if (symbol.Equals("SENSEXBEES", StringComparison.OrdinalIgnoreCase))
            {
                return "SENSEXBEES.BOM";
            }
            return symbol;
        }
    }
}
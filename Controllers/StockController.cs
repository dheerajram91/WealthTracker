using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace WealthTracker.Controllers
{
    [ApiController]
    public class StockController : ControllerBase
    {
        private readonly ILogger<StockController> _logger;
        private readonly string _avApiKey = "QH9E0MVTZJ8DKVCM";

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

            return NotFound(new { error = "No data found in Alpha Vantage" });
        }

        private async Task<List<object>?> GetFromAlphaVantage(string symbol, DateTime start, DateTime end, CancellationToken ct)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var outputSize = (DateTime.Now - start).TotalDays > 90 ? "full" : "compact";

            bool isCrypto = false;
            if (symbol.Equals("BTC", StringComparison.OrdinalIgnoreCase) || 
               symbol.Equals("ETH", StringComparison.OrdinalIgnoreCase) ||
               symbol.Equals("LTC", StringComparison.OrdinalIgnoreCase))
            {
                isCrypto = true;
            }

            var url = isCrypto
                ? $"https://www.alphavantage.co/query?function=DIGITAL_CURRENCY_DAILY&symbol={symbol}&market=USD&apikey={_avApiKey}"
                : $"https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={symbol}&outputsize={outputSize}&apikey={_avApiKey}";
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
    }
}
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
using WealthTracker.Helper;
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
        private readonly DataProcessHelper _dataHelper;

        public PortfolioController(DataProcessHelper dataHelper) {
            _dataHelper = dataHelper;
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
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            DataProcessHelper.Initialize(_csvPath, baseUrl);
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
                    var currentOldDate = await _dataHelper.GetOldestDateAsync(key).ConfigureAwait(false);
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
                var combined = await _dataHelper.GetOrLoadPriceDataAsync(sym).ConfigureAwait(false);
                priceData[sym] = combined;
            }

            // 3. Calculation Logic
            double a = 1.0; // Cumulative growth of X
            double b = 0.0; // Cumulative growth of Y additions

            var startOfSeries = sortedPeriods.First().StartDate;
            var endOfSeries = sortedPeriods.Last().EndDate;

            // Define Monthly Investment Dates (1st of every month within range)
            var monthlyDates = _dataHelper.GetMonthlyDates(startOfSeries, endOfSeries);

            // Calculate Wealth Growth Monthly (X=100, Y=5 for graph1 and X=100, Y=0 for graph2)
            var graphData = new List<MonthlyWealthPoint>();
            var graphDataNoSIP = new List<MonthlyWealthPoint>();

            double currentXGrowth = 1.0;
            var yInvestments = new List<(DateTime Date, double Multiplier)>();

            // Create ComputeHelper once and reuse it (also used later for final simulation)
            ComputeHelper compHelper = new ComputeHelper(_dataHelper);

            foreach (var date in monthlyDates)
            {
                // Update growth factor for X and existing Ys for the month
                var period = sortedPeriods.FirstOrDefault(p => date >= p.StartDate && date <= p.EndDate);
                if (period == null) continue;

                // Convert single period dictionary into a List<PortfolioPeriod> as required by ComputeHelper.GetPortfolioReturn
                var singlePeriodList = new List<PortfolioPeriod> { period };

                double monthlyReturn = compHelper.GetPortfolioReturn(date, date.AddMonths(1).AddDays(-1), singlePeriodList, priceData);
            }

            // Simplified Result based on simulation:
            // Coefficient A: Final Value of 1 unit invested at start
            // Coefficient B: Sum of final values of 1 unit invested every month

            // Perform final simulation to get a, b, XIRR, and Invested amounts
            double xirr, xirrNoSip, invested, investedNoSip;
            (a, b, graphData, graphDataNoSIP, xirr, xirrNoSip, invested, investedNoSip) = compHelper.RunSimulation(sortedPeriods, priceData, 100, 5);

            return Ok(new PortfolioResponse
            {
                CoefficientA = Math.Round(a, 4),
                CoefficientB = Math.Round(b, 4),
                Polynomial = $"{a:F2}X + {b:F2}Y",
                GraphData = graphData,
                GraphDataNoSIP = graphDataNoSIP,
                Xirr = Math.Round(xirr * 100.0, 2),
                XirrNoSIP = Math.Round(xirrNoSip * 100.0, 2),
                Invested = Math.Round(invested, 2),
                InvestedNoSIP = Math.Round(investedNoSip, 2),
                ShareCode = EncodeRequest(request)
            });
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
#pragma warning disable CS8603 // Possible null reference return.
                return JsonSerializer.Deserialize<PortfolioRequest>(json);
#pragma warning restore CS8603 // Possible null reference return.
            }
            catch
            {
                return null;
            }
        }
    }
}

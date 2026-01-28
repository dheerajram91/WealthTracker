using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WealthTracker.Helper;
using WealthTracker.Models;

namespace WealthTracker.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CompareController : ControllerBase
    {
        private readonly string _csvPath = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        private readonly DataProcessHelper _dataHelper;

        public CompareController(DataProcessHelper dataHelper)
        {
            _dataHelper = dataHelper ?? throw new ArgumentNullException(nameof(dataHelper));
        }

        [HttpPost]
        public async Task<IActionResult> Compare([FromBody] CompareRequest request)
        {
            if (request == null || request.CompareItems == null || !request.CompareItems.Any())
                return BadRequest("CompareItems are required.");

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            DataProcessHelper.Initialize(_csvPath, baseUrl);

            // Collect all unique symbols participating in the comparison
            var symbols = request.CompareItems
                                 .SelectMany(ci => ci.Portfolio?.Keys ?? Enumerable.Empty<string>())
                                 .Where(s => !string.IsNullOrWhiteSpace(s))
                                 .Select(s => s.ToUpperInvariant())
                                 .Distinct()
                                 .ToList();

            if (!symbols.Any())
                return BadRequest("At least one symbol is required in CompareItems.");

            // 1) Determine the start date: the maximum (latest) of all symbols' oldest available date
            var oldestDates = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in symbols)
            {
                var od = await _dataHelper.GetOldestDateAsync(sym).ConfigureAwait(false);
                if (od == DateTime.MinValue)
                    return BadRequest($"No historical data available for symbol '{sym}'.");
                oldestDates[sym] = od;
            }

            var startDate = oldestDates.Values.Max();
            var endDate = DateTime.Today;

            // 2) Load price data for all symbols
            var priceData = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sym in symbols)
            {
                var pd = await _dataHelper.GetOrLoadPriceDataAsync(sym).ConfigureAwait(false);
                priceData[sym] = pd ?? new Dictionary<DateTime, double>();
            }

            // 3) For each CompareItem, create a single period from startDate..endDate and run the simulation
            var results = new List<CompareResult>();
            int itemIndex = 1;
            foreach (var item in request.CompareItems)
            {
                if (item.Portfolio == null || !item.Portfolio.Any())
                {
                    results.Add(new CompareResult
                    {
                        Message = "Empty portfolio",
                        GraphData = new List<MonthlyWealthPoint>(),
                        GraphDataNoSIP = new List<MonthlyWealthPoint>()
                    });
                    continue;
                }

                var period = new PortfolioPeriod
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    Items = item.Portfolio.ToDictionary(k => k.Key.ToUpperInvariant(), v => v.Value)
                };
                var periods = new List<PortfolioPeriod> { period };

                var (a, b, graphData, graphDataNoSIP) = RunSimulation(periods, priceData, 100, 5);

                results.Add(new CompareResult
                {
                    Id = $"Strategy-{itemIndex++}",
                    CoefficientA = Math.Round(a, 4),
                    CoefficientB = Math.Round(b, 4),
                    GraphData = graphData,
                    GraphDataNoSIP = graphDataNoSIP
                });
            }

            return Ok(new
            {
                StartDate = startDate,
                EndDate = endDate,
                Results = results
            });
        }

        // Copied/adapted simulation methods (uses _dataHelper internally)
        private (double a, double b, List<MonthlyWealthPoint> graphData, List<MonthlyWealthPoint> graphDataNoSIP) RunSimulation(
            List<PortfolioPeriod> periods,
            Dictionary<string, Dictionary<DateTime, double>> allPriceData,
            double graphX,
            double graphY)
        {
            var startOfSeries = periods.First().StartDate;
            var endOfSeries = periods.First().EndDate;
            var monthlyDates = _dataHelper.GetMonthlyDates(startOfSeries, endOfSeries);

            double coeffA = 1.0;
            double coeffB = 0.0;
            var graphPoints = new List<MonthlyWealthPoint>();
            var graphPoints_no_SIP = new List<MonthlyWealthPoint>();

            DateTime lastCheckDate = startOfSeries;

            foreach (var currentDate in monthlyDates)
            {
                double periodReturn = GetPortfolioReturn(lastCheckDate, currentDate, periods, allPriceData);

                coeffA *= periodReturn;
                coeffB *= periodReturn;

                coeffB += 1.0;

                double currentWealth = (graphX * coeffA) + (graphY * coeffB);
                graphPoints.Add(new MonthlyWealthPoint
                {
                    Month = currentDate.ToString("MMM yyyy"),
                    Wealth = Math.Round(currentWealth, 2)
                });

                currentWealth = (graphX * coeffA);
                graphPoints_no_SIP.Add(new MonthlyWealthPoint
                {
                    Month = currentDate.ToString("MMM yyyy"),
                    Wealth = Math.Round(currentWealth, 2)
                });

                lastCheckDate = currentDate;
            }

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
            var period = periods.FirstOrDefault(p => start >= p.StartDate && start <= p.EndDate) ?? periods.First();

            double totalReturn = 0;

            foreach (var item in period.Items)
            {
                string symbol = item.Key;
                double weight = item.Value / 100.0;

                if (allPriceData.ContainsKey(symbol))
                {
                    var pd = allPriceData[symbol];
                    double priceStart = _dataHelper.GetPriceOnOrBefore(start, pd);
                    double priceEnd = _dataHelper.GetPriceOnOrBefore(end, pd);

                    double assetReturn = priceEnd / priceStart;
                    totalReturn += (assetReturn * weight);
                }
            }

            return totalReturn;
        }
    }
}

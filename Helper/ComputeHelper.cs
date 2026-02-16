using WealthTracker.Models;

namespace WealthTracker.Helper
{
    public class ComputeHelper
    {
        DataProcessHelper dataHelper;

        public ComputeHelper(DataProcessHelper dataHelperParam)
        {
            dataHelper = dataHelperParam;
        }

        public (double a, double b, List<MonthlyWealthPoint> graphData, List<MonthlyWealthPoint> graphDataNoSIP) RunSimulation(
            List<PortfolioPeriod> periods,
            Dictionary<string, Dictionary<DateTime, double>> allPriceData,
            double graphX,
            double graphY)
        {
            var startOfSeries = periods.First().StartDate;
            var endOfSeries = periods.First().EndDate;
            var monthlyDates = dataHelper.GetMonthlyDates(startOfSeries, endOfSeries);

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

        public double GetPortfolioReturn(
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
                    double priceStart = dataHelper.GetPriceOnOrBefore(start, pd);
                    double priceEnd = dataHelper.GetPriceOnOrBefore(end, pd);

                    double assetReturn = priceEnd / priceStart;
                    totalReturn += (assetReturn * weight);
                }
            }

            return totalReturn;
        }
    }
}

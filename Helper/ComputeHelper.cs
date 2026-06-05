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

        public (double a, double b, List<MonthlyWealthPoint> graphData, List<MonthlyWealthPoint> graphDataNoSIP, double xirr, double xirrNoSIP, double invested, double investedNoSIP) RunSimulation(
            List<PortfolioPeriod> periods,
            Dictionary<string, Dictionary<DateTime, double>> allPriceData,
            double graphX,
            double graphY)
        {
            var startOfSeries = periods.First().StartDate;
            var endOfSeries = periods.Last().EndDate;
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

            // Calculate Cash Flows for SIP XIRR
            var cashFlowsSip = new List<(DateTime Date, double Amount)>();
            cashFlowsSip.Add((startOfSeries, -graphX));
            foreach (var currentDate in monthlyDates)
            {
                cashFlowsSip.Add((currentDate, -graphY));
            }
            double finalValSip = (graphX * coeffA) + (graphY * coeffB);
            cashFlowsSip.Add((endOfSeries, finalValSip));

            double xirr = CalculateXirr(cashFlowsSip);
            double invested = graphX + (monthlyDates.Count * graphY);

            // Calculate Cash Flows for No-SIP XIRR
            var cashFlowsNoSip = new List<(DateTime Date, double Amount)>();
            cashFlowsNoSip.Add((startOfSeries, -graphX));
            double finalValNoSip = graphX * coeffA;
            cashFlowsNoSip.Add((endOfSeries, finalValNoSip));

            double xirrNoSip = CalculateXirr(cashFlowsNoSip);
            double investedNoSip = graphX;

            return (coeffA, coeffB, graphPoints, graphPoints_no_SIP, xirr, xirrNoSip, invested, investedNoSip);
        }

        private double CalculateXirr(List<(DateTime Date, double Amount)> cashFlows)
        {
            if (cashFlows == null || cashFlows.Count < 2) return 0.0;

            // If final value is 0 or less, return -1.0 (-100%)
            if (cashFlows.Last().Amount <= 0)
            {
                return -1.0;
            }

            // Bisection method bounds
            double low = -0.999;
            double high = 100.0; // 10000% return
            double tolerance = 1e-6;
            int maxIterations = 100;

            double npvLow = CalculateNpv(low, cashFlows);
            double npvHigh = CalculateNpv(high, cashFlows);

            if (npvLow * npvHigh > 0)
            {
                if (npvLow < 0) return -0.999;
                if (npvHigh > 0) return 100.0;
                return 0.0;
            }

            double mid = 0.0;
            for (int i = 0; i < maxIterations; i++)
            {
                mid = (low + high) / 2.0;
                double npvMid = CalculateNpv(mid, cashFlows);

                if (Math.Abs(npvMid) < tolerance || (high - low) / 2.0 < tolerance)
                {
                    return mid;
                }

                if (npvMid * npvLow > 0)
                {
                    low = mid;
                    npvLow = npvMid;
                }
                else
                {
                    high = mid;
                    npvHigh = npvMid;
                }
            }

            return mid;
        }

        private double CalculateNpv(double r, List<(DateTime Date, double Amount)> cashFlows)
        {
            double npv = 0.0;
            DateTime d1 = cashFlows[0].Date;

            foreach (var cf in cashFlows)
            {
                double t = (cf.Date - d1).TotalDays / 365.0;
                npv += cf.Amount / Math.Pow(1.0 + r, t);
            }

            return npv;
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

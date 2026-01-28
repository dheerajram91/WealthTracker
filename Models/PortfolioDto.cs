namespace WealthTracker.Models
{
    public class PortfolioItem
    {
        public string Symbol { get; set; }
        public double Percentage { get; set; }
    }

    public class PortfolioPeriod
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public Dictionary<string, double> Items { get; set; }
    }

    public class PortfolioRequest
    {
        public List<PortfolioPeriod> Periods { get; set; }
    }

    public class MonthlyWealthPoint
    {
        public string Month { get; set; }
        public double Wealth { get; set; }
    }

    public class PortfolioResponse
    {
        public string Polynomial { get; set; } // e.g., "4.20X + 602.13Y"
        public double CoefficientA { get; set; } // for X
        public double CoefficientB { get; set; } // for Y
        public List<MonthlyWealthPoint> GraphData { get; set; }

        public List<MonthlyWealthPoint> GraphDataNoSIP { get; set; }

        public string ShareCode { get; set; }
    }

    public class CompareRequest
    {
        public List<CompareItem>? CompareItems { get; set; }
    }

    public class CompareItem
    {
        public Dictionary<string, double>? Portfolio { get; set; }
    }

    public class CompareResult
    {
        public string? Id { get; set; }
        public string? Message { get; set; }
        public double CoefficientA { get; set; }
        public double CoefficientB { get; set; }
        public List<MonthlyWealthPoint> GraphData { get; set; } = new();
        public List<MonthlyWealthPoint> GraphDataNoSIP { get; set; } = new();
    }

}

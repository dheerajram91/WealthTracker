using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WealthTracker.DataUpdater
{
    class Program
    {
        static bool _apiLimitExceeded = false;

        static async Task Main(string[] args)
        {
            Console.Title = "WealthTracker CSV Data Auto-Updater";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================");
            Console.WriteLine("        WEALTHTRACKER CSV DATA AUTO-UPDATER       ");
            Console.WriteLine("==================================================");
            Console.ResetColor();

            try
            {
                var dataDir = FindDataDirectory();
                Console.WriteLine($"Resolved Data directory: {dataDir}\n");

                var csvFiles = Directory.GetFiles(dataDir, "*.csv");
                if (csvFiles.Length == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("No CSV files found in the Data directory!");
                    Console.ResetColor();
                    return;
                }

                // Target end date: Last Week (Today - 7 days)
                var today = DateTime.Today;
                var targetEndDate = today.AddDays(-7);
                Console.WriteLine($"Target End Date (Last Week Limit): {targetEndDate:yyyy-MM-dd}");
                Console.WriteLine("--------------------------------------------------");

                int updateCount = 0;

                for (int i = 0; i < csvFiles.Length; i++)
                {
                    var file = csvFiles[i];
                    var symbol = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();

                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($"\n[{i + 1}/{csvFiles.Length}] Processing Symbol: {symbol}");
                    Console.ResetColor();

                    var (lastCsvDate, existingData) = ReadCsv(file);
                    if (lastCsvDate == DateTime.MinValue)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  [WARN] Skipping: File '{symbol}.csv' has no valid data or cannot be parsed.");
                        Console.ResetColor();
                        continue;
                    }

                    Console.WriteLine($"  Last record date in CSV: {lastCsvDate:yyyy-MM-dd}");

                    var gapStartDate = lastCsvDate.AddDays(1);
                    if (gapStartDate > targetEndDate)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  [OK] Up-to-date! No gap to fill (gap start date {gapStartDate:yyyy-MM-dd} exceeds limit {targetEndDate:yyyy-MM-dd}).");
                        Console.ResetColor();
                        continue;
                    }

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  Gap found: {gapStartDate:yyyy-MM-dd} to {targetEndDate:yyyy-MM-dd}");
                    Console.ResetColor();

                    // If it is CASH, we fill it immediately without hitting the AlphaVantage API
                    List<(DateTime Date, double Price)> newPrices;
                    if (symbol.Equals("CASH", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("  CASH symbol detected. Filling baseline prices at 100.0 without API query...");
                        newPrices = new List<(DateTime Date, double Price)>();
                        for (var d = gapStartDate; d <= targetEndDate; d = d.AddDays(1))
                        {
                            newPrices.Add((d, 100.0));
                        }
                    }
                    else if (symbol.Equals("NIFTYBEES", StringComparison.OrdinalIgnoreCase) ||
                             symbol.Equals("SENSEXBEES", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  Indian ETF detected ({symbol}). Routing directly to Yahoo Finance fallback (AlphaVantage does not support Indian indices)...");
                        newPrices = await FetchYahooFinance(symbol, gapStartDate, targetEndDate);
                    }
                    else
                    {
                        Console.WriteLine($"  Querying AlphaVantage API for {symbol} ({gapStartDate:yyyy-MM-dd} -> {targetEndDate:yyyy-MM-dd})...");
                        newPrices = await FetchAlphaVantage(symbol, gapStartDate, targetEndDate);

                        // Respect AlphaVantage API free tier rate limit: 5 requests per minute (we pause for 20s between non-cash/non-Yahoo queries)
                        if (i < csvFiles.Length - 1 && !_apiLimitExceeded)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine("  Enforcing a 20-second rate-limit delay before the next API request...");
                            Console.ResetColor();
                            await Task.Delay(20000);
                        }
                    }

                    // Merge old and new data, avoiding duplicates
                    var merged = new Dictionary<DateTime, double>();
                    foreach (var row in existingData)
                    {
                        merged[row.Date] = row.Price;
                    }
                    foreach (var row in newPrices)
                    {
                        merged[row.Date] = row.Price;
                    }

                    // Always write back sorted chronological ascending in the clean 2-column format
                    var sortedList = merged.OrderBy(x => x.Key).ToList();
                    WriteCsv(file, sortedList);

                    if (newPrices.Count > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  Found {newPrices.Count} new daily prices!");
                        Console.WriteLine($"  [SUCCESS] Successfully appended and updated {symbol}.csv (Total records: {sortedList.Count}).");
                        Console.ResetColor();
                        updateCount++;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  [OK] Parsed and converted {symbol}.csv to clean 2-column format (Total records: {sortedList.Count}).");
                        Console.ResetColor();
                    }
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("\n==================================================");
                Console.WriteLine($"      UPDATER FINISHED (Symbols updated: {updateCount})      ");
                Console.WriteLine("==================================================");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ERROR] An unexpected error occurred: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }
        }

        static string FindDataDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Data");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the 'Data' directory anywhere in the parent hierarchy.");
        }

        static (DateTime lastDate, List<(DateTime Date, double Price)> existingData) ReadCsv(string filePath)
        {
            var list = new List<(DateTime Date, double Price)>();
            var lines = File.ReadAllLines(filePath);

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Try parsing the 2-column yyyy-MM-dd format first
                var parts = line.Split(',');
                if (parts.Length == 2)
                {
                    string dateStr = parts[0].Trim();
                    string priceStr = parts[1].Trim();
                    if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) &&
                        double.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                    {
                        list.Add((d, p));
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
                    if (DateTime.TryParseExact(dateStr.Trim(), new[] { "MMM dd, yyyy", "MMM d, yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) &&
                        double.TryParse(yahooParts[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                    {
                        list.Add((d, p));
                        continue;
                    }
                }

                // Try 7-column standard format with yyyy-MM-dd date
                if (parts.Length >= 6)
                {
                    string dateStr = parts[0].Trim();
                    if (DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) &&
                        double.TryParse(parts[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p))
                    {
                        list.Add((d, p));
                    }
                }
            }

            if (!list.Any())
            {
                return (DateTime.MinValue, list);
            }

            list = list.OrderBy(x => x.Date).ToList();
            return (list.Last().Date, list);
        }

        static void WriteCsv(string filePath, List<KeyValuePair<DateTime, double>> data)
        {
            using (var writer = new StreamWriter(filePath, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("Date,Close");
                foreach (var kvp in data)
                {
                    writer.WriteLine($"{kvp.Key:yyyy-MM-dd},{kvp.Value.ToString("0.######", CultureInfo.InvariantCulture)}");
                }
            }
        }

        static async Task<List<(DateTime Date, double Price)>> FetchAlphaVantage(string symbol, DateTime start, DateTime end)
        {
            var list = new List<(DateTime Date, double Price)>();
            if (_apiLimitExceeded)
            {
                // Fallback to Yahoo Finance when AlphaVantage daily key limit is reached
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    [API FALLBACK] Querying Yahoo Finance for {symbol} due to AlphaVantage key limitation...");
                Console.ResetColor();
                return await FetchYahooFinance(symbol, start, end);
            }
            bool isCrypto = symbol.Equals("BTC", StringComparison.OrdinalIgnoreCase) ||
                            symbol.Equals("ETH", StringComparison.OrdinalIgnoreCase) ||
                            symbol.Equals("LTC", StringComparison.OrdinalIgnoreCase);

            var apiKey = "BVJ2VKR5FAZCZQ3Q";
            // For equities, always use outputsize=compact because outputsize=full is a premium-only feature on the free tier.
            // compact returns the last 100 daily price records (approx. 5 months of trading data), which is free.
            var outputSize = "compact";

            var apiSymbol = MapSymbolForAlphaVantage(symbol);
            var url = isCrypto
                ? $"https://www.alphavantage.co/query?function=DIGITAL_CURRENCY_DAILY&symbol={apiSymbol}&market=USD&apikey={apiKey}"
                : $"https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={apiSymbol}&outputsize={outputSize}&apikey={apiKey}";

            int maxRetries = 3;
            int retryDelayMs = 60000; // 60 seconds to fully reset AlphaVantage rate limit window

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                    client.DefaultRequestHeaders.Add("User-Agent", "C# WealthTracker DataUpdater");

                    var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"    [API ERROR] HTTP Status Code {resp.StatusCode} (Attempt {attempt}/{maxRetries})");
                        Console.ResetColor();
                        if (attempt == maxRetries) return list;
                        await Task.Delay(10000); // Wait 10s and retry HTTP errors
                        continue;
                    }

                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    // Check for rate limit note, information, or error message
                    bool isRateLimited = false;
                    string limitReason = string.Empty;

                    if (doc.RootElement.TryGetProperty("Information", out var info))
                    {
                        isRateLimited = true;
                        limitReason = info.GetString() ?? "Standard API call frequency limit reached.";
                    }
                    else if (doc.RootElement.TryGetProperty("Note", out var note))
                    {
                        isRateLimited = true;
                        limitReason = note.GetString() ?? "Call frequency note received.";
                    }
                    else if (doc.RootElement.TryGetProperty("Error Message", out var errMsg))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"    [API ERROR] {errMsg.GetString()}");
                        Console.ResetColor();
                        return list; // Error Message means invalid symbol/key - no point in retrying
                    }

                    if (isRateLimited)
                    {
                        // Check if it's a premium feature message instead of a rate limit (no point in retrying)
                        if (limitReason.Contains("premium", StringComparison.OrdinalIgnoreCase) || 
                            limitReason.Contains("subscribe", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"    [API LIMITATION] {limitReason}");
                            Console.ResetColor();
                            if (limitReason.Contains("subscribe", StringComparison.OrdinalIgnoreCase))
                            {
                                _apiLimitExceeded = true;
                            }
                            return await FetchYahooFinance(symbol, start, end);
                        }

                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    [RATE LIMIT DETECTED] {limitReason}");
                        if (attempt < maxRetries)
                        {
                            Console.WriteLine($"    Waiting {retryDelayMs / 1000} seconds to clear rate limit window and retry (Attempt {attempt}/{maxRetries})...");
                            Console.ResetColor();
                            await Task.Delay(retryDelayMs);
                            continue;
                        }
                        else
                        {
                            Console.WriteLine("    Maximum retries reached. Skipping this symbol.");
                            Console.ResetColor();
                            return list;
                        }
                    }

                    var rootProp = isCrypto ? "Time Series (Digital Currency Daily)" : "Time Series (Daily)";
                    if (!doc.RootElement.TryGetProperty(rootProp, out var timeSeries))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("    [API WARN] 'Time Series' property not found in JSON response.");
                        Console.ResetColor();
                        return list;
                    }

                    foreach (var prop in timeSeries.EnumerateObject())
                    {
                        if (DateTime.TryParse(prop.Name, out DateTime date))
                        {
                            if (date >= start && date <= end)
                            {
                                var valElement = prop.Value;
                                string? closeStr = null;

                                if (isCrypto)
                                {
                                    if (valElement.TryGetProperty("4a. close (USD)", out var closeUsdProp))
                                    {
                                        closeStr = closeUsdProp.GetString();
                                    }
                                    else if (valElement.TryGetProperty("4. close", out var closeGenProp))
                                    {
                                        closeStr = closeGenProp.GetString();
                                    }
                                }
                                else
                                {
                                    if (valElement.TryGetProperty("4. close", out var closeProp))
                                    {
                                        closeStr = closeProp.GetString();
                                    }
                                }

                                if (closeStr != null && double.TryParse(closeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double price))
                                {
                                    list.Add((date, price));
                                }
                            }
                        }
                    }

                    // Success - exit the retry loop
                    break;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"    [EXCEPTION] Fetch failed: {ex.Message} (Attempt {attempt}/{maxRetries})");
                    Console.ResetColor();
                    if (attempt == maxRetries) return await FetchYahooFinance(symbol, start, end);
                    await Task.Delay(10000);
                }
            }

            if (list.Count == 0)
            {
                return await FetchYahooFinance(symbol, start, end);
            }
            return list.OrderBy(x => x.Date).ToList();
        }

        static string MapSymbolForAlphaVantage(string symbol)
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

        static async Task<List<(DateTime Date, double Price)>> FetchYahooFinance(string symbol, DateTime start, DateTime end)
        {
            var list = new List<(DateTime Date, double Price)>();
            
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
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    return list;
                }

                var json = await resp.Content.ReadAsStringAsync();
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
                                    list.Add((date, price));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    [Yahoo Fallback Info] Fetch failed for {symbol}: {ex.Message}");
                Console.ResetColor();
            }

            return list.OrderBy(x => x.Date).ToList();
        }
    }
}

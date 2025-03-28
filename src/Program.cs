﻿using System.Diagnostics;
using Microsoft.Playwright;
using Microsoft.Extensions.Configuration;
using static Scraper.CosmosDB;
using static Scraper.Utilities;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Azure.Cosmos.Linq;

// New World Scraper
// -----------------
// Scrapes product info and pricing from New World NZ's website.

namespace Scraper
{
    public class Program
    {
        static int secondsDelayBetweenPageScrapes = 11;
        static bool uploadToDatabase = false;
        static bool uploadImages = false;
        static bool useHeadlessBrowser = false;

        public record Product(
            string id,
            string name,
            string? size,
            float currentPrice,
            string[] category,
            string sourceSite,
            DatedPrice[] priceHistory,
            DateTime lastUpdated,
            DateTime lastChecked,
            float? unitPrice,
            string? unitName,
            float? originalUnitQuantity
        );
        public record DatedPrice(DateTime date, float price);

        // Singletons for Playwright
        public static IPlaywright? playwright;
        public static IPage? playwrightPage;
        public static IBrowser? browser;
        public static HttpClient httpclient = new HttpClient();

        // Get config from appsettings.json
        public static IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) //load base settings
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true) //load local settings
            .AddEnvironmentVariables()
            .Build();

        public static async Task Main(string[] args)
        {
            // Handle command-line arguments 'db', 'images', 'headed'
            foreach (string arg in args)
            {
                if (arg.Contains("db"))
                {
                    // dotnet run db - will scrape and upload data to a database
                    uploadToDatabase = true;

                    // Connect to CosmosDB - end program if unable to connect
                    if (!await CosmosDB.EstablishConnection(
                        db: "supermarket-prices",
                        partitionKey: "/name",
                        container: "products"
                    )) return;
                }

                // dotnet run db custom-query - will run a pre-defined sql query
                if (arg.Contains("custom-query"))
                {
                    await CustomQuery();
                    return;
                }

                // dotnet run db images - will scrape, then upload both data and images
                if (arg.Contains("images"))
                {
                    uploadImages = true;
                }

                if (arg.Contains("headed"))
                {
                    useHeadlessBrowser = false;
                }

                if (arg.Contains("headless"))
                {
                    useHeadlessBrowser = true;
                }
            }
            if (!uploadToDatabase)
            {
                // dotnet run - will scrape and display results in console
                LogWarn("(Dry Run Mode)");
            }

            // Start Stopwatch for logging purposes
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            // Establish Playwright browser
            await EstablishPlaywright(useHeadlessBrowser);

            // Read lines from Urls.txt file - end program if unable to read
            List<string>? lines = ReadLinesFromFile("Urls.txt");
            if (lines == null) return;

            // Parse and optimise each line into valid urls to be scraped
            List<CategorisedURL> categorisedUrls =
                ParseTextLinesIntoCategorisedURLs(
                    lines,
                    urlShouldContain: "newworld.co.nz",
                    replaceQueryParamsWith: "",
                    queryOptionForEachPage: "pg=",
                    incrementEachPageBy: 1
                );

            // Log how many pages will be scraped
            LogWarn(
                $"{categorisedUrls.Count} pages to be scraped, " +
                $"with {secondsDelayBetweenPageScrapes}s delay between each page scrape."
            );

            // Open an initial page and allow geolocation set the desired store location
            await OpenInitialPageAndSetLocation();

            // Open up each URL and run the scraping function
            for (int i = 0; i < categorisedUrls.Count(); i++)
            {
                try
                {
                    // Separate out url from categorisedUrl
                    string url = categorisedUrls[i].url;

                    // Create shortened url for logging
                    string shortenedLoggingUrl = HttpUtility.UrlDecode(url)
                        .Replace("https://www.", "")
                        .Replace("shop/category/", "")
                        .Replace("&refinementList[category2NI]", " ")
                        .Trim();

                    shortenedLoggingUrl = Regex.Replace(shortenedLoggingUrl, @"\[\d\]=", "- ");

                    // Log current sequence of page scrapes, the total num of pages to scrape
                    LogWarn(
                        $"\n[{i + 1}/{categorisedUrls.Count()}] {shortenedLoggingUrl}"
                    );

                    // Try load page and wait for full content to dynamically load in
                    await playwrightPage!.GotoAsync(url);

                    // Scroll down page to trigger lazy loading
                    for (int scrollLoop = 0; scrollLoop < 3; scrollLoop++)
                    {
                        await playwrightPage.Keyboard.PressAsync("PageDown");
                        Thread.Sleep(120);
                    }

                    // Wait for prices to load in
                    string price =
                        await playwrightPage.GetByTestId("price-dollars").Last.InnerHTMLAsync();

                    // Get all div elements
                    var allDivElements =
                        await playwrightPage.QuerySelectorAllAsync("div");

                    // Filter down to only product div elements which contain a special attribute
                    List<IElementHandle> productElements = new List<IElementHandle>();
                    foreach (var div in allDivElements)
                    {
                        try
                        {
                            // Try get the data-testid attribute of each div element
                            var dataTestId = div.GetAttributeAsync("data-testid").Result;
                            if (dataTestId != null && (dataTestId.Contains("-EA-000") || dataTestId.Contains("-KGM-000")))
                            {
                                productElements.Add(div);
                            }
                        }
                        catch (Exception)
                        {
                            // Ignore divs that cannot parse a data-testid
                        }
                    }

                    // Log how many valid products were found on this page
                    Log(
                        $"{productElements.Count} Products Found \t" +
                        $"Total Time Elapsed: {stopwatch.Elapsed.Minutes}:{stopwatch.Elapsed.Seconds.ToString().PadLeft(2, '0')}\t" +
                        $"Category: {categorisedUrls[i].category}"
                    );

                    // Create per-page counters for logging purposes
                    int newCount = 0, priceUpdatedCount = 0, nonPriceUpdatedCount = 0, upToDateCount = 0;

                    // Loop through every found playwright element
                    foreach (var productElement in productElements)
                    {
                        // Create validated Product object from playwright element
                        Product? scrapedProduct =
                            await ScrapeProductElementToRecord(
                                productElement,
                                url,
                                new string[] { categorisedUrls[i].category }
                            );

                        if (uploadToDatabase && scrapedProduct != null)
                        {
                            // Try upsert to CosmosDB
                            UpsertResponse response = await CosmosDB.UpsertProduct(scrapedProduct);

                            // Increment stats counters based on response from CosmosDB
                            switch (response)
                            {
                                case UpsertResponse.NewProduct:
                                    newCount++;
                                    break;
                                case UpsertResponse.PriceUpdated:
                                    priceUpdatedCount++;
                                    break;
                                case UpsertResponse.NonPriceUpdated:
                                    nonPriceUpdatedCount++;
                                    break;
                                case UpsertResponse.AlreadyUpToDate:
                                    upToDateCount++;
                                    break;
                                case UpsertResponse.Failed:
                                default:
                                    break;
                            }

                            if (uploadImages)
                            {
                                // Get hi-res image url
                                string hiResImageUrl = await GetHiresImageUrl(productElement);

                                // Use a REST API to upload product image
                                if (hiResImageUrl != "" && hiResImageUrl != null)
                                {
                                    await UploadImageUsingRestAPI(hiResImageUrl, scrapedProduct);
                                }
                            }
                        }
                        else if (!uploadToDatabase && scrapedProduct != null)
                        {
                            // In Dry Run mode, print a log row for every product
                            string unitString = scrapedProduct.unitPrice != null ?
                                "$" + scrapedProduct.unitPrice + " /" + scrapedProduct.unitName : "";

                            Log(
                                scrapedProduct!.id.PadLeft(9) + " | " +
                                scrapedProduct.name!.PadRight(60).Substring(0, 60) + " | " +
                                scrapedProduct.size!.PadRight(10) + " | $" +
                                scrapedProduct.currentPrice.ToString().PadLeft(5) + " | " +
                                unitString
                            );
                        }
                    }

                    if (uploadToDatabase)
                    {
                        // Log consolidated CosmosDB stats for entire page scrape
                        LogWarn(
                            $"{"CosmosDB:"} {newCount} new products, " +
                            $"{priceUpdatedCount} prices updated, {nonPriceUpdatedCount} info updated, " +
                            $"{upToDateCount} already up-to-date"
                        );
                    }
                }
                catch (TimeoutException)
                {
                    LogError("Unable to Load Web Page - timed out after 30 seconds");
                }
                catch (PlaywrightException e)
                {
                    LogError("Unable to Load Web Page - " + e.Message);
                }
                catch (Exception e)
                {
                    LogError(e.ToString());
                    return;
                }

                // This page has now completed scraping. A delay is added in-between each subsequent URL
                if (i != categorisedUrls.Count() - 1)
                {
                    Thread.Sleep(secondsDelayBetweenPageScrapes * 1000);
                }
            }

            // Try clean up playwright browser and other resources, then end program
            try
            {
                Log("Scraping Completed \n");
                await playwrightPage!.Context.CloseAsync();
                await playwrightPage.CloseAsync();
                await browser!.CloseAsync();
            }
            catch (Exception)
            {
            }
            return;
        }

        public async static Task EstablishPlaywright(bool headless)
        {
            try
            {
                // Launch Playwright Browser - Headless mode doesn't work with some anti-bot mechanisms,
                //  so a regular browser window may be required.
                playwright = await Playwright.CreateAsync();
                browser = await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = headless }
                );

                // Launch Page 
                playwrightPage = await browser.NewPageAsync();

                // Route exclusions, such as ads, trackers, etc
                await RoutePlaywrightExclusions();
                return;
            }
            catch (PlaywrightException)
            {
                LogError(
                    "Browser must be manually installed using: \n" +
                    "pwsh bin/Debug/net6.0/playwright.ps1 install\n"
                );
                throw;
            }
        }

        // Get the hi-res image url from the Playwright element
        public async static Task<string> GetHiresImageUrl(IElementHandle productElement)
        {
            // Image URL - The last img tag contains the product image
            var imgDiv = await productElement.QuerySelectorAllAsync("a > div > img");
            string? imgUrl = await imgDiv.Last().GetAttributeAsync("src");

            // Check if image is a valid product image, otherwise return blank
            if (!imgUrl!.Contains("fsimg.co.nz/product/retail/fan/image/")) return "";

            // Swap url from 200x200 or 400x400 to master to get the hi-res version
            imgUrl = Regex.Replace(imgUrl, @"\d00x\d00", "master");

            return imgUrl;
        }

        // ScrapeProductElementToRecord()
        // ------------------------------
        // Takes a playwright element "div.fs-product-card", scrapes each of the desired data fields,
        // and then returns a validated Product record

        private async static Task<Product?> ScrapeProductElementToRecord(
            IElementHandle productElement,
            string sourceUrl,
            string[] category
        )
        {
            // Product Name, Size, Dollar and Cent Price as strings
            string name = "", size = "", dollarString = "", centString = "";

            // Get all <p> elements, then loop through each and assign values 
            // based on the data-testid attribute.
            var allPElements = await productElement.QuerySelectorAllAsync("p");
            foreach (var p in allPElements)
            {
                string? pType = "";
                // Try get a data-testid attribute,
                try
                {
                    pType = await p.GetAttributeAsync("data-testid");
                }
                // If not found, catch the exception and skip to the next <p>
                catch (Exception)
                {
                    continue;
                }

                switch (pType)
                {
                    // Scrape name
                    case "product-title":
                        name = await p.InnerTextAsync();
                        break;

                    // Scrape size
                    case "product-subtitle":
                        size = await p.InnerTextAsync();
                        size = size.Replace("l", "L");  // capitalize L for litres
                        if (size == "kg") size = "per kg";
                        break;

                    // Scrape price dollar and cent elements.
                    // If a multi-buy and single-buy price exists, the single-buy price will 
                    // happen later in the loop and will be set as the final price
                    case "price-dollars":
                        dollarString = await p.InnerTextAsync();
                        break;

                    case "price-cents":
                        centString = await p.InnerTextAsync();
                        break;

                    default:
                        break;
                }
            }

            // Price - Combine dollar and cent strings, then parse into a float
            // ----------------------------------------------------------------
            float currentPrice;
            try
            {
                currentPrice = float.Parse(dollarString.Trim() + "." + centString.Trim());
            }
            catch (NullReferenceException)
            {
                // No price is listed for this product, so can ignore
                return null;
            }
            catch (Exception e)
            {
                LogError($"{name} - Couldn't scrape price info\n{e.GetType()}");
                return null;
            }

            // Image URL & Product ID
            // ----------------------
            string id;
            try
            {
                // Image Url - The last img tag contains the product image
                var imgDiv = await productElement.QuerySelectorAllAsync("a > div > img");
                string? imgUrl = await imgDiv.Last().GetAttributeAsync("src");

                // ID - get product ID from image filename
                var imageFilename = imgUrl!.Split("/").Last();  // get the last /section of the url
                imageFilename = imageFilename.Split("?").First(); // remove any query params
                id = "N" + imageFilename.Split(".").First();    // prepend N to ID
            }
            catch (Exception e)
            {
                LogError($"{name} - Couldn't scrape image URL\n{e.GetType()}");
                return null;
            }

            // try
            // {
            //     // If product size is listed as 'ea' or 'pk' and a unit price is listed,
            //     // try to derive the full product size from the unit price
            //     var unitPriceDiv = await productElement.QuerySelectorAsync(".fs-product-card__price-per-unit");
            //     if (Regex.IsMatch(size, @"\d?(ea|pk)\b") && unitPriceDiv != null)
            //     {
            //         // unitPriceFullString contains the full unit price format: $2.40/100g
            //         string unitPriceFullString = await unitPriceDiv.InnerTextAsync();

            //         // unitPrice contains just the price, such as 2.40
            //         float unitPricing = float.Parse(unitPriceFullString.Split("/")[0].Replace("$", ""));

            //         // unitPriceQuantity contains just the unit quantity, such as 100
            //         string unitPriceQuantityString =
            //             Regex.Match(unitPriceFullString.Split("/")[1], @"\d+").ToString();

            //         // If no unit quantity digits are found, default to 1 unit
            //         if (unitPriceQuantityString == "") unitPriceQuantityString = "1";
            //         float unitPriceQuantity = float.Parse(unitPriceQuantityString);

            //         // unitPriceName will end up as g|kg|ml|L
            //         string unitPriceName =
            //             Regex.Match(unitPriceFullString.Split("/")[1].ToLower(), @"(g|kg|ml|l)").ToString();
            //         unitPriceName = unitPriceName.Replace("l", "L");

            //         // Find how many units fit within the full product price
            //         float numUnitsForFullProductSize = currentPrice / unitPricing;

            //         // Multiply the unit price quantity to get the full product size
            //         float fullProductSize = numUnitsForFullProductSize * unitPriceQuantity;

            //         // Round product size based on what unit is used, then set final product size
            //         if (unitPriceName == "g" || unitPriceName == "ml")
            //         {
            //             size = Math.Round(fullProductSize) + unitPriceName;
            //         }
            //         else if (unitPriceName == "kg" || unitPriceName == "L")
            //         {
            //             size = Math.Round(fullProductSize, 2) + unitPriceName;
            //         }
            //     }
            // }
            // catch (Exception e)
            // {
            //     LogError($"{name} - Couldn't derive unit price\n{e.GetType()}");
            //     return null;
            // }

            try
            {
                // Check for manual product data overrides based on product ID
                SizeAndCategoryOverride overrides = CheckProductOverrides(id);

                // If override lists the product as invalid, ignore this product
                if (overrides.category == "invalid")
                    throw new Exception(name + " is overridden as an invalid product.");

                // If override lists a sizes or category, use these instead of the scraped values.
                if (overrides.size != "") size = overrides.size;
                if (overrides.category != "") category = new string[] { overrides.category };

                // Set source website
                string sourceSite = "newworld.co.nz";

                // Create a DateTime object for the current time, but set minutes and seconds to zero
                DateTime todaysDate = DateTime.UtcNow;
                todaysDate = new DateTime(
                    todaysDate.Year,
                    todaysDate.Month,
                    todaysDate.Day,
                    todaysDate.Hour,
                    0,
                    0
                );

                // Create a DatedPrice for the current time and price
                DatedPrice todaysDatedPrice = new DatedPrice(todaysDate, currentPrice);

                // Create Price History array with a single element
                DatedPrice[] priceHistory = new DatedPrice[] { todaysDatedPrice };

                // Get derived unit price and unit name
                string? unitPriceString = DeriveUnitPriceString(size, currentPrice);
                float? unitPrice = null;
                string? unitName = "";
                float? originalUnitQuantity = null;
                if (unitPriceString != null)
                {
                    unitPrice = float.Parse(unitPriceString.Split("/")[0]);
                    unitName = unitPriceString.Split("/")[1];
                    originalUnitQuantity = float.Parse(unitPriceString.Split("/")[2]);
                }

                // Create product record with above values
                Product product = new Product(
                    id,
                    name!,
                    size,
                    currentPrice,
                    category,
                    sourceSite,
                    priceHistory,
                    todaysDate,
                    todaysDate,
                    unitPrice,
                    unitName,
                    originalUnitQuantity
                );

                // Validate then return completed product
                if (IsValidProduct(product)) return product;
                else throw new Exception(product.name);
            }
            catch (Exception e)
            {
                LogError($"{name} - Price scrape error: \n{e.GetType()}");
                // Return null if any exceptions occurred during scraping
                return null;
            }
        }

        // OpenInitialPageAndSetLocation()
        // -------------------------------
        private static async Task OpenInitialPageAndSetLocation()
        {
            try
            {
                // Set geo-location data
                await SetGeoLocation();

                // Goto any page to trigger geo-location detection
                await playwrightPage!.GotoAsync("https://www.newworld.co.nz/");

                // Wait for page to automatically reload with the new geo-location
                Thread.Sleep(4000);
                await playwrightPage.WaitForSelectorAsync("div.js-quick-links");

                LogWarn($"Selected Store: {await GetStoreLocationName()}");
                return;
            }
            catch (Exception e)
            {
                LogError(e.ToString());
                throw;
            }
        }

        // GetStoreLocationName()
        // ----------------------
        // Get the name of the store location that is currently active
        private static async Task<string> GetStoreLocationName()
        {
            try
            {
                // Get the top ribbon - desktop version
                var topRibbon = await playwrightPage!.QuerySelectorAsync(".fs-ribbon-items__layout.desktop-only");

                // Get the first span element, which contains the store name
                var storeLocationElement = await topRibbon!.QuerySelectorAsync("span");

                // Return the store name
                return await storeLocationElement!.InnerTextAsync();
            }
            catch (PlaywrightException)
            {
                LogError("Error loading playwright browser, check firewall and network settings");
                throw;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // SetGeoLocation()
        // ----------------
        private static async Task SetGeoLocation()
        {
            float latitude, longitude;

            // Try get latitude and longitude from appsettings.json
            try
            {
                if (
                    config.GetSection("GEOLOCATION_LAT").Value == "" ||
                    config.GetSection("GEOLOCATION_LONG").Value == ""
                    )
                {
                    throw new ArgumentNullException();
                }
                latitude = float.Parse(config.GetSection("GEOLOCATION_LAT").Value!);
                longitude = float.Parse(config.GetSection("GEOLOCATION_LONG").Value!);

                // Set playwright geolocation using found latitude and longitude
                await playwrightPage!.Context.SetGeolocationAsync(
                    new Geolocation() { Latitude = latitude, Longitude = longitude }
                );

                // Grant permission to access geo-location
                await playwrightPage.Context.GrantPermissionsAsync(new string[] { "geolocation" });

                // Log to console
                LogWarn($"Selecting closest store using geo-location: ({latitude}, {longitude})");
            }

            // Return if no latitude and longitude are found
            catch (ArgumentNullException)
            {
                LogWarn("Using default location");
                return;
            }

            // Return if unable to parse values or for any other exception
            catch (Exception)
            {
                LogWarn(
                    "Invalid geolocation found in appsettings.json, ensure format is:\n" +
                    "\"GEOLOCATION_LAT\": \"-41.21\"," +
                    "\"GEOLOCATION_LONG\": \"174.91\""
                );
                return;
            }
        }

        // RoutePlaywrightExclusions()
        // ---------------------------
        // Excludes playwright from downloading unwanted resources such as ads, trackers, images, etc.

        private static async Task RoutePlaywrightExclusions(bool logToConsole = false)
        {
            // Define excluded types and urls to reject
            string[] typeExclusions = { "image", "media", "font", "other" };
            string[] urlExclusions = { "googleoptimize.com", "gtm.js", "visitoridentification.js" +
            "js-agent.newrelic.com", "challenge-platform" };
            List<string> exclusions = urlExclusions.ToList<string>();

            // Route with exclusions processed
            await playwrightPage!.RouteAsync("**/*", async route =>
            {
                var req = route.Request;
                bool excludeThisRequest = false;
                string trimmedUrl = req.Url.Length > 120 ? req.Url.Substring(0, 120) + "..." : req.Url;

                foreach (string exclusion in exclusions)
                {
                    if (req.Url.Contains(exclusion)) excludeThisRequest = true;
                }
                if (typeExclusions.Contains(req.ResourceType)) excludeThisRequest = true;

                if (excludeThisRequest)
                {
                    if (logToConsole) LogError($"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.AbortAsync();
                }
                else
                {
                    if (logToConsole) Log($"{req.Method} {req.ResourceType} - {trimmedUrl}");
                    await route.ContinueAsync();
                }
            });
        }
    }
}

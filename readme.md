# New World NZ Supermarket Scraper

Scrapes product pricing and info from New World NZ's website and logs the data to the console.

The data is laid out the same as PaknSave's website most of the time, but these sites go through major changes on different intervals.

Product information and price snapshots can be optionally stored into Azure CosmosDB.
Images can be sent to an API for resizing, analysis and other processing.

The scraper is powered by `Microsoft Playwright`, requiring `.NET 8.0` & `Powershell` to run.

## Quick Setup

With `.NET SDK` installed, clone this repo, change directory into `/src`, then restore and build .NET packages with:

```powershell
dotnet restore
dotnet build
```

Playwright Chromium web browser can be installed using:

```cmd
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
```

The program is now ready to use and will scrape all default URLs in `Urls.txt`.

```cmd
dotnet run
```

## Optional Setup with appsettings.json

To set optional advanced parameters, edit `appsettings.json`.

If using CosmosDB, set the CosmosDB endpoint and key using the format:

```json
{
  "COSMOS_ENDPOINT": "<your cosmosdb endpoint uri>",
  "COSMOS_KEY": "<your cosmosdb primary key>"
}
```

To override the default store location with a specific location, set geolocation co-ordinates in Long/Lat format. Long/Lat co-ordinates can be obtained from resources such as google maps.
The closest store location to the co-ordinates will be selected when running the scraper.

```json
{
    "GEOLOCATION_LONG": "174.91",
    "GEOLOCATION_LAT": "-41.21",
}
```

## Command-Line Usage

To dry run the scraper and log each product to the console:

```powershell
dotnet run
```

Optional arguments can be mixed and match for advanced usage:

To store scraped data to the cosmosdb database (defined in appsettings).

```powershell
dotnet run db
```

Images can be sent off for processing to an azure func url (defined in appsettings).
```powershell
dotnet run db images
```

The browser defaults to headed mode for debugging and reliability, but can be tested as headless with:
```powershell
dotnet run headless
```

## Sample Dry Run Output

```cmd
       ID | Name                                 | Size       | Price  | Unit Price
----------------------------------------------------------------------------------
 N5046509 | Celery Half                          | ea         | $ 1.99 | 
 N5281734 | Pams Strawberry Tomatoes             | 250g       | $ 3.49 | $13.96 /kg
 N5046651 | Crown Cut Pumpkin                    | per kg     | $ 2.99 | $2.99 /kg
 N5040461 | Pams Fresh Baby Cos Lettuce          | 120g       | $ 4.19 | $34.92 /kg
 N5040724 | LeaderBrand Baby Spinach             | 120g       | $ 4.49 | $37.42 /kg
 N5040468 | Pams Mesclun Salad                   | 120g       | $ 4.19 | $34.92 /kg
```

## Sample Product Stored in CosmosDB

This sample was re-run on multiple days to capture changing prices.

```json
{
    "id": "N1234567",
    "name": "Coconut Supreme Slice",
    "size": "350g",
    "currentPrice": 5.89,
    "category": [
        "biscuits"
    ],
    "priceHistory": [
        {
            "date": "2023-05-04T01:00:00",
            "price": 5.89
        }
        {
            "date": "2023-01-02T01:00:00",
            "price": 5.49
        }
    ],
    "unitPrice": 16.83,
    "unitName": "kg",
}
```

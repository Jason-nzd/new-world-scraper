# New World Scraper
[![.NET](https://img.shields.io/badge/.NET-8.0+-5C2D91?logo=dotnet)](https://dotnet.microsoft.com/)
[![Playwright](https://img.shields.io/badge/Playwright-Extra-2EAD33?logo=playwright)](https://playwright.dev/)

A powerful web scraper that extracts product pricing and information from the **New World NZ** website. Logs data to console and optionally stores price history in Azure CosmosDB.
The data is laid out the same as PaknSave's website most of the time, but these sites go through major changes on different intervals.

## ✨ Features
- 🎭 **Headless Browser** — Powered by **Playwright** & **PlaywrightExtraSharp2**
- 📊 **Price Tracking** — Store historical price data to track changes over time
- 🖼️ **Image Processing** — Send product images to a REST API for resizing and analysis
- 📍 **Location-Aware** — Configure store location using geolocation coordinates

## 🚀 Quick Start

### Prerequisites
- [.NET SDK 8.0 or newer](https://dotnet.microsoft.com/download)

### Installation
```bash
# Clone the repository
git clone https://github.com/Jason-nzd/pakn-scraper
cd src

# Restore and build dependencies
dotnet restore
dotnet build

# Run the scraper (dry run mode)
dotnet run
```

> 💡The first run will automatically install Playwright browser runtimes.

### 📋 Sample Console Output
```cmd
       ID | Name                             | Size       | Price  | Unit Price
----------------------------------------------------------------------------------
 N5046509 | Celery Half                      | ea         | $ 1.99 | 
 N5281734 | Pams Strawberry Tomatoes         | 250g       | $ 3.49 | $13.96 /kg
 N5046651 | Crown Cut Pumpkin                | per kg     | $ 2.99 | $2.99 /kg
 N5040461 | Pams Fresh Baby Cos Lettuce      | 120g       | $ 4.19 | $34.92 /kg
 N5040724 | LeaderBrand Baby Spinach         | 120g       | $ 4.49 | $37.42 /kg
 N5040468 | Pams Mesclun Salad               | 120g       | $ 4.19 | $34.92 /kg
```

## ⚙️ appsettings.json

> Optional advanced parameters can be configured in `appsettings.json`:

Store product information and price snapshots in CosmosDB by defining:

```json
{
  "COSMOS_ENDPOINT": "<your-cosmosdb-endpoint-uri>",
  "COSMOS_KEY": "<your-cosmosdb-primary-key>",
  "COSMOS_DB": "<database-name>",
  "COSMOS_CONTAINER": "<container-name>"
}
```
Override the default store location by setting geolocation coordinates (Longitude/Latitude):
```json
{
  "GEOLOCATION_LONG": "174.91",
  "GEOLOCATION_LAT": "-41.21"
}
```
> 💡 Coordinates can be obtained from Google Maps or similar services. The website will geolocate to the closest store.

Send scraped product images to a REST API for processing:
```json
{
  "IMAGE_PROCESS_API_URL": "<your-rest-api-endpoint>"
}
```

### 💻 Command-Line Usage
- ```dotnet run``` - Dry run - logs products to console only
- ```dotnet run db``` - Stores scraped data into CosmosDB
- ```dotnet run db images``` - Store to DB + send images to API
- ```dotnet run headed``` - Run with visible browser for debugging

### 📄 Sample Cosmos DB Document
Products stored in Cosmos DB include price history for trend analysis:

```json
{
    "id": "N1234567",
    "name": "Coconut Supreme Slice",
    "size": "350g",
    "category": "chocolate",
    "priceHistory": [
        {
            "date": "2023-07-02",
            "price": 2.95
        },
        {
            "date": "2024-12-30",
            "price": 3.25
        },
        {
            "date": "2025-06-30",
            "price": 3.4
        },
        {
            "date": "2025-12-29",
            "price": 3.85
        }
    ],
    "lastChecked": "2026-01-04",
    "unitPrice": "6.70/kg"
}
```
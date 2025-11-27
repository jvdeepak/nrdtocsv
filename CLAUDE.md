# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NRDToCSV is a **NinjaTrader 8 AddOn** written in C# that converts NRD (`.nrd`) market replay files to CSV format using the undocumented `MarketReplay.DumpMarketDepth` API.

## Build and Deployment

This is a NinjaTrader 8 AddOn - there is no standalone build process. Development workflow:

1. Import the AddOn into NinjaTrader 8 via `Tools > Import > NinjaScript Add-On...`
2. NinjaTrader compiles the C# code internally
3. Access via `Tools > NRD to CSV` in NinjaTrader

For releases, export the compiled AddOn as a zip file to the `Releases/` folder.

## Architecture

Single-file AddOn (`AddOns/NRDToCSV.cs`) with three classes:

- **NRDToCSV** (`AddOnBase`): Registers the menu item in NinjaTrader's Control Center under Tools
- **NRDToCSVWindow** (`NTWindow`): Main WPF UI with conversion logic
  - Parallel conversion using `PARALLEL_THREADS_COUNT` (default: 4 threads)
  - Regex-based filtering of NRD files by instrument/date
  - Progress tracking with ETA calculation
  - Skips already-converted files
- **DumpEntry**: Data class for conversion queue items

## Key Technical Details

- Uses `NinjaTrader.Data.MarketReplay.DumpMarketDepth()` for actual conversion
- Date parsing from filename: expects `YYYYMMDD` format in NRD filenames
- Output path: `Documents/NinjaTrader 8/db/replay.csv/{instrument}/{date}.csv`
- Input path: `Documents/NinjaTrader 8/db/replay/{instrument}/`

## CSV Output Format

Two record types (L1 for Level 1, L2 for Level 2 data):
- L1: `type;MarketDataType;timestamp;offset;price;volume`
- L2: `type;MarketDataType;timestamp;offset;operation;position;marketmaker;price;volume`

Timestamps are in local NinjaTrader timezone with 100-nanosecond precision offset.

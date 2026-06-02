import sys
import os
import pandas as pd

def main():
    html_path = None
    csv_path = None

    # Support both CLI arguments and interactive prompts
    if len(sys.argv) >= 3:
        html_path = sys.argv[1]
        csv_path = sys.argv[2]
    else:
        print("==================================================")
        print("      YAHOO FINANCE HTML TO CLEAN CSV CONVERTER    ")
        print("==================================================")
        html_path = input("Enter path to the input Yahoo Finance HTML file: ").strip().strip('\'"')
        csv_path = input("Enter path to the (new) output CSV file: ").strip().strip('\'"')

    if not html_path or not csv_path:
        print("[ERROR] Both input HTML path and output CSV path must be provided.")
        return

    if not os.path.exists(html_path):
        print(f"[ERROR] Input HTML file not found: {html_path}")
        return

    try:
        print(f"Reading HTML file: {html_path}...")
        tables = pd.read_html(html_path)
        if not tables:
            print("[ERROR] No tables found in the HTML file.")
            return

        df = tables[0]
        
        # 1. Identify Date and Close columns dynamically
        date_col = None
        for col in df.columns:
            if 'date' in str(col).lower():
                date_col = col
                break
        
        close_col = None
        # First search for "close*" or exact match "close"
        for col in df.columns:
            col_str = str(col).lower()
            if 'close*' in col_str or col_str == 'close':
                close_col = col
                break
        # Fallback to any column containing "close"
        if not close_col:
            for col in df.columns:
                if 'close' in str(col).lower():
                    close_col = col
                    break

        if not date_col or not close_col:
            print(f"[ERROR] Could not identify columns. Found Date: {date_col}, Close: {close_col}")
            print(f"Available columns: {list(df.columns)}")
            return

        print(f"Identified columns - Date: '{date_col}', Close: '{close_col}'")

        # 2. Filter out non-numeric row entries (e.g. Dividend/Split rows)
        # Drop rows where Date or Close is null
        df = df.dropna(subset=[date_col, close_col])

        # Exclude rows containing descriptive texts like "dividend" or "split"
        mask = df.astype(str).apply(lambda x: x.str.contains('dividend|split|capital gain', case=False)).any(axis=1)
        df = df[~mask]

        # Convert Date to datetime, drop any rows that fail parsing
        df['parsed_date'] = pd.to_datetime(df[date_col], errors='coerce')
        df = df.dropna(subset=['parsed_date'])

        # Convert Close to numeric, drop any rows that fail parsing
        df['parsed_close'] = pd.to_numeric(df[close_col], errors='coerce')
        df = df.dropna(subset=['parsed_close'])

        # 3. Restructure to modern 2-column format (Date,Close)
        clean_df = pd.DataFrame({
            'Date': df['parsed_date'].dt.strftime('%Y-%m-%d'),
            'Close': df['parsed_close']
        })

        # 4. Sort chronologically ascending (latest values at the bottom / oldest at top)
        clean_df = clean_df.sort_values(by='Date', ascending=True)

        # 5. Output to CSV
        output_dir = os.path.dirname(csv_path)
        if output_dir and not os.path.exists(output_dir):
            os.makedirs(output_dir, exist_ok=True)

        clean_df.to_csv(csv_path, index=False)
        print("--------------------------------------------------")
        print(f"[SUCCESS] Cleaned data saved successfully!")
        print(f"Destination: {csv_path}")
        print(f"Total Records: {len(clean_df)}")
        print(f"Date Range: {clean_df['Date'].iloc[0]} to {clean_df['Date'].iloc[-1]}")
        print("==================================================")

    except Exception as e:
        print(f"[ERROR] Conversion failed: {e}")

if __name__ == '__main__':
    main()
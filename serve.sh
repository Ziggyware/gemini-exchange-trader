#!/bin/bash
# Simple server for the GEMX Idle Trader Dashboard
# Usage: ./serve.sh [port]
PORT=${1:-8080}
echo "⬡ GEMX Idle Trader Dashboard"
echo "  Serving on http://0.0.0.0:$PORT"
echo "  Press Ctrl+C to stop"
cd "$(dirname "$0")"
python3 -m http.server "$PORT" --bind 0.0.0.0 2>/dev/null || \
python -m SimpleHTTPServer "$PORT" 2>/dev/null || \
echo "No Python found. Open dashboard.html directly in a browser."
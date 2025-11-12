#!/bin/bash

# Go to the folder where this script is located
cd "$(dirname "$0")"

# Set the port your app uses
PORT=44398

# Kill any process using that port
PID=$(lsof -ti tcp:$PORT)
if [ -n "$PID" ]; then
    echo "Killing process $PID using port $PORT..."
    kill -9 $PID
else
    echo "No process found on port $PORT"
fi

# Start the solution
echo "Starting solution..."
dotnet run

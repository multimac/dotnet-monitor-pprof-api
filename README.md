# .NET Monitor - pprof API

This is a small wrapper around the [dotnet-monitor](https://github.com/dotnet/dotnet-monitor) tool which converts the .nettrace profiling information it outputs into the pprof format.

It currently only works with CPU profiling, though additional profiling may be added in the future.

## Testing

A `docker-compose.yml` file is included which can be used to launch this tool, along with Grafana and Grafana Phlare. To run it locally, you will need to launch `dotnet-monitor` and the program you wish to collect profiling information from.

1) Launch `dotnet-monitor` with the following command...

    ```dotnet-monitor collect --no-auth --urls http://localhost:52323```

1) Update the environment variable `Controller__DotnetMonitorUrl` in `docker-compose.yml` to point `dotnet-monitor-pprof-api` to  `dotnet-monitor`. Make sure to update the `pid` query parameter to point to the ID of the process you want to profile.

1) Run `docker-compose up` and then navigate to [http://localhost:3000](http://localhost:3000)

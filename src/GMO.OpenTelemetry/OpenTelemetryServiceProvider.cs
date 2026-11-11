namespace GMO.OpenTelemetry
{
    // Collector must stay first: as value 0 it is the default when configuration omits the provider.
    public enum OpenTelemetryServiceProvider
    {
        Collector,
        NewRelic,
        Dynatrace
    }
}

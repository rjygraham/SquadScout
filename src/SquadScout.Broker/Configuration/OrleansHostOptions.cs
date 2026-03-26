namespace SquadScout.Broker.Configuration;

public sealed class OrleansHostOptions
{
    public const string SectionName = "Orleans";
    public const string DefaultStorageProvider = "AdoNetStore";
    public const string DefaultAdoNetInvariant = "Microsoft.Data.Sqlite";

    public bool Enabled { get; set; }

    public string ClusterId { get; set; } = "squadscout-local";

    public string ServiceId { get; set; } = "SquadScout.Broker";

    public int SiloPort { get; set; } = 11111;

    public int GatewayPort { get; set; } = 30000;

    public string StorageProvider { get; set; } = DefaultStorageProvider;

    public string AdoNetInvariant { get; set; } = DefaultAdoNetInvariant;

    public string SqliteConnectionString { get; set; } = "Data Source=.squadscout\\orleans\\squadscout.db;Mode=ReadWriteCreate;Cache=Shared";

    public bool AutoInitializeSchema { get; set; } = true;
}

namespace QQAlchemyDesktop.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQAlchemyDesktop");
        Data = Path.Combine(Root, "data");
        Properties = Path.Combine(Data, "properties");
        Screenshots = Path.Combine(Root, "screenshots");
        Logs = Path.Combine(Root, "logs");
        Database = Path.Combine(Root, "state.db");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Properties);
        Directory.CreateDirectory(Screenshots);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }
    public string Data { get; }
    public string Properties { get; }
    public string Screenshots { get; }
    public string Logs { get; }
    public string Database { get; }
}

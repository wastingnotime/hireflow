using MongoDB.Driver;
using WastingNoTime.HireFlow.Applications.Models;

namespace WastingNoTime.HireFlow.Applications.Data;

public sealed class ApplicationsDb
{
    private readonly IMongoDatabase _db;

    public ApplicationsDb(IMongoClient client, IConfiguration config)
    {
        var dbName =
            Environment.GetEnvironmentVariable("APPLICATIONS_MONGO_DB") ??
            config["APPLICATIONS_MONGO_DB"] ??
            "hireflow_applications";

        _db = client.GetDatabase(dbName);
    }

    public IMongoCollection<Application> Applications =>
        _db.GetCollection<Application>("applications");

    public IMongoCollection<Interview> Interviews =>
        _db.GetCollection<Interview>("interviews");
}

using MongoDB.Driver;
using WastingNoTime.HireFlow.Candidates.Api.Models;

namespace WastingNoTime.HireFlow.Candidates.Api.Data;

public sealed class CandidatesDb
{
    private readonly IMongoDatabase _db;

    public CandidatesDb(IMongoClient client, IConfiguration config)
    {
        var dbName = config["CANDIDATES_MONGO_DB"] 
                     ?? "hireflow_candidates";
        _db = client.GetDatabase(dbName);
    }

    public IMongoCollection<Application> Applications =>
        _db.GetCollection<Application>("applications");

    public IMongoCollection<Interview> Interviews =>
        _db.GetCollection<Interview>("interviews");
}

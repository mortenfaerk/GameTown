using EFModel.Models;
using RestSharp;
using Newtonsoft.Json;
namespace API.Services
{
    public class RAWGService
    {
        private DatabaseContext DatabaseContext { get; set; }
        private string APIKey { get; set; }

        public RAWGService(DatabaseContext databaseContext, string apikey)
        {
                DatabaseContext = databaseContext;
                APIKey = apikey;
        }

        public async Task<Game> GetGameById(int id)
        {
            var client = new RestClient();
            var request = new RestRequest($"https://api.rawg.io/api/games/{id}", Method.Get).AddParameter("key",APIKey);
            RestResponse response = await client.ExecuteAsync(request);
            Game result = JsonConvert.DeserializeObject<Game>(response.Content);
            //TODO: Handle null checking here
            return result;
            
        }

    }
}

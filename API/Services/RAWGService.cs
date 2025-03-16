using EFModel.Models;
using RestSharp;
using Newtonsoft.Json;
namespace API.Services
{
    public class RAWGService(string apikey)
    {
        private string APIKey { get; set; } = apikey;

        public async Task<Game?> GetGameById(int id)
        {
            var client = new RestClient();
            var request = new RestRequest($"https://api.rawg.io/api/games/{id}", Method.Get).AddParameter("key",APIKey);
            RestResponse response = await client.ExecuteAsync(request);

            if (response.Content == null)
                return null;
            Game? result = JsonConvert.DeserializeObject<Game?>(response.Content);
            return result;
      
        }

    }
}

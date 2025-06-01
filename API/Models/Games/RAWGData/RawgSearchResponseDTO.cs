using EFModel.Models;
using System.Text.Json.Serialization;

namespace API.Models.Games.RAWGData
{
    public class RAWGSearchResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }
        [JsonPropertyName("next")]
        public string? Next { get; set; }
        [JsonPropertyName("previous")]
        public string? Previous { get; set; }
        [JsonPropertyName("results")]
        public List<Rawggame>? Results { get; set; }
        [JsonPropertyName("user_platforms")]
        public bool? User_platforms { get; set; }
    }
}

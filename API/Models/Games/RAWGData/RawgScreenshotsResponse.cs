using EFModel.Models;
using System.Text.Json.Serialization;

namespace API.Models.Games.RAWGData
{
    public class RawgScreenshotsResponse
    {
        [JsonPropertyName("count")]
        public required int Count { get; set; }
        [JsonPropertyName("next")]
        public string? Next { get; set; }
        [JsonPropertyName("previous")]
        public string? Previous { get; set; }
        [JsonPropertyName("results")]
        public Rawgscreenshot[]? Results { get; set; }


    }
}

using EFModel.Models;

namespace API.Models.Games
{
    public class RAWGSearchResponse
    {
        public int count { get; set; }
        public string? next { get; set; }
        public string? previous { get; set; }
        public List<Rawggame>? results { get; set; }
        public bool? user_platforms { get; set; }
    }
}

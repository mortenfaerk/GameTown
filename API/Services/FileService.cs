namespace API.Services
{
    public class FileService
    {
        readonly string _gameDirectory;

        public FileService(string gameDirectory)
        {
            _gameDirectory = gameDirectory;
        }

        public string GetGameFilePath(string fileName) {
            return Path.Combine(_gameDirectory, fileName);
        }
    }
}

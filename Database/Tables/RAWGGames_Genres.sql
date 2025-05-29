CREATE TABLE [dbo].[RAWGGames_Genres] (
    [game_id] INT,
    [genre_id] INT,
    PRIMARY KEY ([game_id], [genre_id]),
    FOREIGN KEY ([game_id]) REFERENCES [RAWGGames]([id]),
    FOREIGN KEY ([genre_id]) REFERENCES [RAWGGenres]([id])
);
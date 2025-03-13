CREATE TABLE [GameGenres] (
    [game_id] INT,
    [genre_id] INT,
    PRIMARY KEY ([game_id], [genre_id]),
    FOREIGN KEY ([game_id]) REFERENCES [Games]([id]),
    FOREIGN KEY ([genre_id]) REFERENCES [Genres]([id])
);
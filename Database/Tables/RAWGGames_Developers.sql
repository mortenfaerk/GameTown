CREATE TABLE [dbo].[RAWGGames_Developers] (
    [game_id] INT,
    [developer_id] INT,
    PRIMARY KEY ([game_id], [developer_id]),
    FOREIGN KEY ([game_id]) REFERENCES [RAWGGames]([id]),
    FOREIGN KEY ([developer_id]) REFERENCES [RAWGDevelopers]([id])
);
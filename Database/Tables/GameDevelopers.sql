CREATE TABLE [GameDevelopers] (
    [game_id] INT,
    [developer_id] INT,
    PRIMARY KEY ([game_id], [developer_id]),
    FOREIGN KEY ([game_id]) REFERENCES [Games]([id]),
    FOREIGN KEY ([developer_id]) REFERENCES [Developers]([id])
);
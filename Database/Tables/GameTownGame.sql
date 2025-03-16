CREATE TABLE [dbo].[GameTownGame]
(
	[Id] uniqueidentifier NOT NULL PRIMARY KEY DEFAULT(newid()),
	[Title] NVARCHAR(500) NOT NULL,
	[HowTo] NVARCHAR(MAX) NOT NULL,
	[GameId] int NULL, 
	[URL] NVARCHAR(500) NOT NULL,
    CONSTRAINT [FK_GameTownGame_Games] FOREIGN KEY ([GameId]) REFERENCES [Games]([id])

)

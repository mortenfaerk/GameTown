CREATE TABLE [dbo].[RAWGGames_Screenshots]
(
	[gameid] INT NOT NULL,
	[screenshotid] INT NOT NULL ,
	PRIMARY KEY ([gameid], [screenshotid]),
	FOREIGN KEY ([gameid]) REFERENCES [RAWGGames]([id]),
	FOREIGN KEY ([screenshotid]) REFERENCES [RAWGScreenshots]([Id])
)

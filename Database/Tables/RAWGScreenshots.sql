CREATE TABLE [dbo].[RAWGScreenshots]
(
	[Id] INT NOT NULL PRIMARY KEY,
	[image] NVARCHAR(500) NOT NULL,
	[width] int NOT NULL,
	[height] int NOT NULL,
	[is_deleted] bit NOT NULL DEFAULT 0,
)

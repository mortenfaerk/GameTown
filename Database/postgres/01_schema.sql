-- GameTown — PostgreSQL schema
-- Translated from the retired SSDT project (Database/Tables/*.sql).
--
-- Identifiers are deliberately double-quoted to preserve the original SQL Server
-- casing ("GameTownGame", "RAWGGames", "PasswordHash", ...). Npgsql scaffolding
-- emits explicit ToTable/HasColumnName for these, so the generated EFModel and the
-- API DTO mapping stay byte-for-byte compatible with the SQL Server model.
--
-- Requires PostgreSQL 13+ for the built-in gen_random_uuid().

BEGIN;

-- ---------------------------------------------------------------- GameTown core

CREATE TABLE "GameTownRoles" (
    "Id"           uuid         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "Role"         varchar(50)  NOT NULL,
    "CreatedBy"    varchar(50)  NOT NULL,
    "CreatedDate"  timestamptz  NOT NULL DEFAULT now(),
    "ModifiedBy"   varchar(50)  NOT NULL,
    "ModifiedDate" timestamptz  NOT NULL DEFAULT now(),
    "IsActive"     boolean      NOT NULL DEFAULT true
);

CREATE TABLE "GameTownUsers" (
    "Id"             uuid         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "PasswordHash"   varchar(256) NOT NULL,
    "Salt"           varchar(256) NOT NULL,
    "Username"       varchar(256) NOT NULL UNIQUE,
    "DisplayName"    varchar(256) NULL,
    "IsActive"       boolean      NOT NULL DEFAULT true,
    "Notes"          varchar(512) NULL,
    "CreatedAt"      timestamptz  NOT NULL DEFAULT now(),
    "CreatedBy"      varchar(256) NULL,
    "LastModifiedAt" timestamptz  NULL DEFAULT now(),
    "LastModifiedBy" varchar(256) NULL
);

CREATE TABLE "GameTownUsers_Roles" (
    "APIUserId" uuid NOT NULL,
    "APIRoleId" uuid NOT NULL,
    CONSTRAINT "PK_APIUsers_APIRoles" PRIMARY KEY ("APIUserId", "APIRoleId"),
    CONSTRAINT "FK_APIUsers_APIRoles_APIUserId" FOREIGN KEY ("APIUserId") REFERENCES "GameTownUsers" ("Id"),
    CONSTRAINT "FK_APIUsers_APIRoles_APIRoleId" FOREIGN KEY ("APIRoleId") REFERENCES "GameTownRoles" ("Id")
);

CREATE TABLE "RefreshTokens" (
    "Id"        uuid         NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "Token"     varchar(512) NOT NULL,
    "ExpiresAt" timestamptz  NOT NULL,
    "IsRevoked" boolean      NOT NULL DEFAULT false,
    "CreatedAt" timestamptz  NOT NULL DEFAULT now(),
    "UserId"    uuid         NOT NULL,
    CONSTRAINT "FK_RefreshTokens_Users" FOREIGN KEY ("UserId") REFERENCES "GameTownUsers" ("Id") ON DELETE CASCADE
);

-- ---------------------------------------------------------------- RAWG entities
-- RAWG rows reuse RAWG's own integer ids, so no identity/default on these PKs.

CREATE TABLE "RAWGGames" (
    "id"                        integer      NOT NULL PRIMARY KEY,
    "slug"                      varchar(255) NOT NULL,
    "name"                      varchar(255) NOT NULL,
    "name_original"             varchar(255) NULL,
    "description"               text         NOT NULL,
    "metacritic"                integer      NULL,
    "released"                  date         NULL,
    "tba"                       boolean      NULL,
    "updated"                   timestamp    NULL,
    "background_image"          varchar(500) NULL,
    "background_image_additional" varchar(500) NULL,
    "website"                   varchar(500) NOT NULL,
    "rating"                    double precision NULL,
    "rating_top"                integer      NULL,
    "playtime"                  integer      NULL,
    "screenshots_count"         integer      NULL,
    "movies_count"              integer      NULL,
    "creators_count"            integer      NULL,
    "achievements_count"        integer      NULL,
    "parent_achievements_count" integer      NULL,
    "reddit_url"                varchar(500) NULL,
    "reddit_count"              integer      NULL,
    "twitch_count"              integer      NULL,
    "youtube_count"             integer      NULL,
    "reviews_text_count"        integer      NULL,
    "ratings_count"             integer      NULL,
    "suggestions_count"         integer      NULL,
    "metacritic_url"            varchar(500) NULL,
    "parents_count"             integer      NULL,
    "additions_count"           integer      NULL,
    "game_series_count"         integer      NULL,
    "reviews_count"             integer      NULL,
    "saturated_color"           varchar(10)  NULL,
    "dominant_color"            varchar(10)  NULL
);

CREATE TABLE "RAWGDevelopers" (
    "id"               integer      NOT NULL PRIMARY KEY,
    "name"             varchar(255) NULL,
    "slug"             varchar(255) NULL,
    "games_count"      integer      NULL,
    "image_background" varchar(500) NULL
);

CREATE TABLE "RAWGGenres" (
    "id"               integer      NOT NULL PRIMARY KEY,
    "name"             varchar(255) NULL,
    "slug"             varchar(255) NULL,
    "image_background" varchar(500) NULL
);

CREATE TABLE "RAWGScreenshots" (
    "Id"         integer      NOT NULL PRIMARY KEY,
    "image"      varchar(500) NOT NULL,
    "width"      integer      NOT NULL,
    "height"     integer      NOT NULL,
    "is_deleted" boolean      NOT NULL DEFAULT false
);

-- ---------------------------------------------------------------- join tables

CREATE TABLE "RAWGGames_Developers" (
    "game_id"      integer NOT NULL,
    "developer_id" integer NOT NULL,
    PRIMARY KEY ("game_id", "developer_id"),
    FOREIGN KEY ("game_id")      REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("developer_id") REFERENCES "RAWGDevelopers" ("id")
);

CREATE TABLE "RAWGGames_Genres" (
    "game_id"  integer NOT NULL,
    "genre_id" integer NOT NULL,
    PRIMARY KEY ("game_id", "genre_id"),
    FOREIGN KEY ("game_id")  REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("genre_id") REFERENCES "RAWGGenres" ("id")
);

CREATE TABLE "RAWGGames_Screenshots" (
    "gameid"       integer NOT NULL,
    "screenshotid" integer NOT NULL,
    PRIMARY KEY ("gameid", "screenshotid"),
    FOREIGN KEY ("gameid")       REFERENCES "RAWGGames" ("id"),
    FOREIGN KEY ("screenshotid") REFERENCES "RAWGScreenshots" ("Id")
);

-- ---------------------------------------------------------------- GameTown games
-- Declared last: FK depends on "RAWGGames".

CREATE TABLE "GameTownGame" (
    "Id"         uuid             NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    "Title"      varchar(500)     NOT NULL,
    "HowTo"      text             NOT NULL,
    "RAWGGameId" integer          NULL,
    "URL"        varchar(500)     NOT NULL,
    "Size"       double precision NOT NULL,  -- size in Mb
    CONSTRAINT "FK_GameTownGame_Games" FOREIGN KEY ("RAWGGameId") REFERENCES "RAWGGames" ("id")
);

COMMIT;

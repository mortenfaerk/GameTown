// Wire contracts shared with the API. These replace the hand-copied view models that used to
// live under GameTownApp/Models — those drifted from the server (Developers/Genres never bound)
// which is exactly what the shared Contracts project exists to prevent.
// Global usings apply to .razor files too, so components can use these without extra @using lines.
global using GameTown.Contracts.Auth;
global using GameTown.Contracts.Games;
global using GameTown.Contracts.Users;

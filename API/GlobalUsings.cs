// Wire contracts shared with the Blazor client, plus the entity <-> contract mapping
// extensions. Made global so endpoints/services read the same as before the Contracts split.
global using GameTown.Contracts.Auth;
global using GameTown.Contracts.Games;
global using GameTown.Contracts.Users;
global using GameTown.Contracts.Settings;
global using API.Mapping;

// The Web SDK's implicit usings pull in Microsoft.AspNetCore.Identity.Data.LoginRequest,
// which collides with ours. Alias so the contract wins everywhere.
global using LoginRequest = GameTown.Contracts.Auth.LoginRequest;

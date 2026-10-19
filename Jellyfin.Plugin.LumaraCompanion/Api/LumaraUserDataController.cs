// Lumara Companion for Jellyfin
// Copyright (C) 2026 Lumara
// Licensed under the GNU General Public License v3.0 (see LICENSE).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.LumaraCompanion.UserData;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LumaraCompanion.Api;

/// <summary>
/// Per-user lists (bookmarks, hidden titles, Spoiler Guard) and reviews,
/// under <c>/Lumara</c>.
/// </summary>
/// <remarks>
/// Every endpoint needs a signed-in user. Lists are private to their owner.
/// Reviews are readable by everyone signed in; each person changes only their
/// own, and admins can remove anyone's.
/// </remarks>
[ApiController]
[Route("Lumara")]
[Authorize]
public class LumaraUserDataController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly UserDataStore _lists;
    private readonly ReviewStore _reviews;
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="LumaraUserDataController"/> class.
    /// </summary>
    /// <param name="lists">Per-user lists.</param>
    /// <param name="reviews">Reviews.</param>
    /// <param name="users">Jellyfin's users, for reviewer names.</param>
    public LumaraUserDataController(UserDataStore lists, ReviewStore reviews, IUserManager users)
    {
        _lists = lists;
        _reviews = reviews;
        _users = users;
    }

    /// <summary>
    /// The signed-in user's list.
    /// </summary>
    /// <param name="kind">bookmarks, hidden or spoiler.</param>
    /// <returns>The list as a JSON object.</returns>
    [HttpGet("UserData/{kind}")]
    public IActionResult GetList([FromRoute] string kind)
    {
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!UserDataStore.IsKind(kind))
        {
            return NotFound();
        }

        return Content(_lists.Read(kind, userId), "application/json");
    }

    /// <summary>
    /// Replaces the signed-in user's list.
    /// </summary>
    /// <param name="kind">bookmarks, hidden or spoiler.</param>
    /// <returns>204, 400 for invalid JSON, 413 if too large.</returns>
    [HttpPut("UserData/{kind}")]
    public async Task<IActionResult> PutList([FromRoute] string kind)
    {
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!UserDataStore.IsKind(kind))
        {
            return NotFound();
        }

        using var reader = new StreamReader(Request.Body);
        var buffer = new char[UserDataStore.MaxBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory()).ConfigureAwait(false);
        if (read > UserDataStore.MaxBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var json = new string(buffer, 0, read);
        if (!UserDataStore.Validate(json, out var error))
        {
            return BadRequest(error);
        }

        _lists.Write(kind, userId, json);
        return NoContent();
    }

    /// <summary>
    /// A title's reviews, newest first.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>The reviews.</returns>
    [HttpGet("Reviews/{itemId}")]
    public ActionResult<IReadOnlyList<Review>> GetReviews([FromRoute] Guid itemId)
    {
        return Ok(_reviews.List(itemId));
    }

    /// <summary>
    /// Adds or replaces the signed-in user's review of a title.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="request">Rating and text.</param>
    /// <returns>The saved review, or 400.</returns>
    [HttpPut("Reviews/{itemId}")]
    public ActionResult<Review> PutReview([FromRoute] Guid itemId, [FromBody] ReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!ReviewStore.Validate(request.Rating, request.Text, out _, out var error))
        {
            return BadRequest(error);
        }

        var name = _users.GetUserById(userId)?.Username ?? string.Empty;
        return Ok(_reviews.Upsert(itemId, userId, name, request.Rating, request.Text));
    }

    /// <summary>
    /// Removes a review: your own, or (admins) anyone's via <paramref name="userId"/>.
    /// </summary>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <param name="userId">Optional reviewer to remove (admins only).</param>
    /// <returns>204, or 403 when removing someone else's without being an admin.</returns>
    [HttpDelete("Reviews/{itemId}")]
    public IActionResult DeleteReview([FromRoute] Guid itemId, [FromQuery] Guid? userId)
    {
        if (!TryUserId(out var me))
        {
            return Unauthorized();
        }

        var target = userId ?? me;
        if (target != me && !User.IsInRole("Administrator"))
        {
            return Forbid();
        }

        _reviews.Remove(itemId, target);
        return NoContent();
    }

    private bool TryUserId(out Guid userId)
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out userId) && userId != Guid.Empty;
    }
}

/// <summary>Body of <c>PUT /Lumara/Reviews/{itemId}</c>.</summary>
public sealed class ReviewRequest
{
    /// <summary>Gets or sets the rating, 1 to 10.</summary>
    public int Rating { get; set; }

    /// <summary>Gets or sets the optional text.</summary>
    public string? Text { get; set; }
}

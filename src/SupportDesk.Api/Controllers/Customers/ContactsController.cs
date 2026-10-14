using Microsoft.AspNetCore.Mvc;
using SupportDesk.Application.Customers;
using SupportDesk.Contracts.Common;
using SupportDesk.Contracts.Customers;

namespace SupportDesk.Api.Controllers.Customers;

/// <summary>Contacts: search, detail, identities, consent, portal access, duplicate detection and merge.</summary>
[Route("api/v1/contacts")]
public sealed class ContactsController(ContactService contacts, ContactMergeService merge) : DeskControllerBase
{
    [HttpGet]
    public Task<PagedResult<ContactListItemDto>> List([FromQuery] ContactQuery query, CancellationToken ct) => contacts.ListAsync(query, ct);

    [HttpGet("{id:guid}")]
    public Task<ContactDetailDto> Get(Guid id, CancellationToken ct) => contacts.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<ContactDetailDto>> Create(SaveContactRequest request, CancellationToken ct)
    {
        var k = await contacts.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = k.Id }, await contacts.GetAsync(k.Id, ct));
    }

    [HttpPut("{id:guid}")]
    public async Task<ContactDetailDto> Update(Guid id, SaveContactRequest request, CancellationToken ct)
    {
        await contacts.UpdateAsync(id, request, ct);
        return await contacts.GetAsync(id, ct);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await contacts.DeleteAsync(id, ct);
        return NoContent();
    }

    // ───── Identities, consent, portal ─────

    [HttpPost("{id:guid}/identities")]
    public Task<ContactIdentityDto> AddIdentity(Guid id, AddContactIdentityRequest request, CancellationToken ct) => contacts.AddIdentityAsync(id, request, ct);

    [HttpDelete("{id:guid}/identities/{identityId:guid}")]
    public async Task<IActionResult> RemoveIdentity(Guid id, Guid identityId, CancellationToken ct)
    {
        await contacts.RemoveIdentityAsync(id, identityId, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/consents")]
    public Task<ContactConsentDto> SetConsent(Guid id, SetConsentRequest request, CancellationToken ct) => contacts.SetConsentAsync(id, request, ct);

    [HttpPut("{id:guid}/portal-access")]
    public async Task<ContactDetailDto> SetPortalAccess(Guid id, SetPortalAccessRequest request, CancellationToken ct)
    {
        await contacts.SetPortalAccessAsync(id, request, ct);
        return await contacts.GetAsync(id, ct);
    }

    [HttpPost("{id:guid}/portal-invite")]
    public Task<PortalInviteResultDto> PortalInvite(Guid id, PortalInviteRequest request, CancellationToken ct) => contacts.SendPortalAccessAsync(id, request, ct);

    // ───── Duplicates & merge ─────

    [HttpGet("{id:guid}/duplicates")]
    public Task<List<DuplicateContactCandidateDto>> DuplicatesOf(Guid id, [FromQuery] double? minScore, CancellationToken ct) => merge.DuplicatesOfAsync(id, minScore, ct);

    [HttpGet("duplicates")]
    public Task<List<DuplicatePairDto>> Duplicates([FromQuery] Guid? customerId, [FromQuery] double? minScore, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        merge.ScanAsync(customerId, minScore, limit, ct);

    [HttpPost("merge")]
    public Task<ContactMergeResultDto> Merge(MergeContactsRequest request, CancellationToken ct) => merge.MergeAsync(request, ct);

    [HttpGet("{id:guid}/merge-history")]
    public Task<List<ContactMergeLogDto>> MergeHistory(Guid id, CancellationToken ct) => merge.MergeHistoryAsync(id, ct);
}

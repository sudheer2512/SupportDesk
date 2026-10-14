import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  ApiError,
  CONSENT_PURPOSES,
  ContactConsentDto,
  ContactDetailDto,
  ContactMergeLogDto,
  ContactMergeResultDto,
  ContactsApi,
  DuplicateContactCandidateDto,
  PORTAL_ACCESS_SCOPES,
  PortalAccessScope,
  PortalInviteResultDto,
  saveErrorMessage,
} from '@sd/core';
import {
  SdAvatar,
  SdBadge,
  SdBreadcrumb,
  SdButton,
  SdCard,
  SdDialogService,
  SdEmptyState,
  SdIcon,
  SdInput,
  SdPageHeader,
  SdRelativeTimePipe,
  SdDateTimePipe,
  SdSkeleton,
  ToastService,
} from '@sd/ui';
import { C360TicketsComponent } from './c360-tickets.component';
import { ContactFormData, ContactFormDialog } from './contact-form.dialog';
import { MergeWizardData, MergeWizardDialog } from './merge-wizard.dialog';
import { HumanizePipe } from './shared/customer-ui';
import { SECTION_STYLES, TABLE_STYLES } from './shared/styles';

const CONSENT_CHANNELS = ['Email', 'Sms', 'WhatsApp', 'Phone'];

/** /customers/contacts/:id — contact profile, identities, consent, portal access, tickets, duplicates and merge history. */
@Component({
  selector: 'desk-contact-detail-page',
  imports: [
    FormsModule,
    RouterLink,
    SdPageHeader,
    SdButton,
    SdBadge,
    SdCard,
    SdAvatar,
    SdIcon,
    SdInput,
    SdSkeleton,
    SdEmptyState,
    SdRelativeTimePipe,
    SdDateTimePipe,
    HumanizePipe,
    C360TicketsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="desk-page">
      @if (notFound()) {
        <sd-empty-state icon="users" title="Contact not found" message="It may have been deleted, or it belongs to a customer outside your accounts." />
      } @else if (!contact()) {
        <div class="desk-page__body pad"><sd-skeleton [lines]="8" /></div>
      } @else {
        @let c = contact()!;
        <sd-page-header [title]="c.fullName" [subtitle]="c.jobTitle" icon="user" [breadcrumbs]="crumbs()">
          @if (c.permissions.edit) {
            <button sdButton sdPageActions icon="pencil" (click)="edit()">Edit</button>
          }
          @if (c.permissions.edit) {
            <button sdButton sdPageActions variant="ghost" icon="trash" iconOnly aria-label="Delete contact" (click)="remove()"></button>
          }
        </sd-page-header>
        <div class="desk-page__body">
          @if (c.status === 'Merged') {
            <p class="merged" role="status">
              <sd-icon name="merge" [size]="16" /> This contact was merged.
              @if (c.mergedIntoContactId) {
                <a [routerLink]="['/customers/contacts', c.mergedIntoContactId]">Open the surviving contact</a>
              }
            </p>
          }
          <div class="grid">
            <div class="stack">
              <sd-card>
                <div class="who">
                  <sd-avatar [name]="c.fullName" size="lg" />
                  <div>
                    <div class="badges">
                      <sd-badge [tone]="c.status === 'Active' ? 'success' : 'neutral'" dot>{{ c.status }}</sd-badge>
                      @if (c.isVip) {
                        <sd-badge tone="warning" icon="star">VIP</sd-badge>
                      }
                      @if (c.isPrimary) {
                        <sd-badge tone="primary">Primary contact</sd-badge>
                      }
                    </div>
                    @if (c.customerId) {
                      <a class="cust" [routerLink]="['/customers', c.customerId]">{{ c.customerName }}</a>
                    }
                  </div>
                </div>
                <dl class="meta">
                  <dt>E-mail</dt>
                  <dd>{{ c.email ?? '—' }}@if (c.emailVerifiedAt) { <sd-icon name="circle-check" [size]="12" aria-label="verified" /> }</dd>
                  <dt>Phone</dt>
                  <dd>{{ c.phone ?? '—' }}</dd>
                  <dt>Mobile</dt>
                  <dd>{{ c.mobile ?? '—' }}</dd>
                  <dt>WhatsApp</dt>
                  <dd>{{ c.whatsAppNumber ?? '—' }}</dd>
                  <dt>Location</dt>
                  <dd>{{ c.locationName ?? '—' }}</dd>
                  <dt>Department</dt>
                  <dd>{{ c.customerDepartmentName ?? '—' }}</dd>
                  <dt>Prefers</dt>
                  <dd>{{ c.preferredChannel ?? 'No preference' }} · {{ c.preferredLanguage }}</dd>
                  <dt>Tickets</dt>
                  <dd>{{ c.stats.openTickets }} open / {{ c.stats.totalTickets }} total@if (c.stats.csatAverage !== null) { · CSAT {{ c.stats.csatAverage.toFixed(1) }} }</dd>
                  <dt>Created</dt>
                  <dd>{{ c.createdAt | sdRelativeTime }}</dd>
                </dl>
              </sd-card>

              <sd-card title="Channel identities" subtitle="Used to match incoming e-mail, calls and messages to this contact.">
                @if (c.identities.length === 0) {
                  <p class="empty-note">No identities.</p>
                } @else {
                  <ul class="ids">
                    @for (i of c.identities; track i.id) {
                      <li>
                        <sd-icon [name]="channelIcon(i.channel)" [size]="14" />
                        <span class="sd-mono sd-small">{{ i.identifier }}</span>
                        <span class="sd-xs sd-muted">{{ i.channel }}</span>
                        @if (i.isVerified) {
                          <sd-badge tone="success" size="sm">Verified</sd-badge>
                        }
                        @if (c.permissions.edit) {
                          <button sdButton size="sm" variant="ghost" icon="x" iconOnly [attr.aria-label]="'Remove ' + i.identifier" (click)="removeIdentity(i.id)"></button>
                        }
                      </li>
                    }
                  </ul>
                }
                @if (c.permissions.edit) {
                  <form class="add-id" (ngSubmit)="addIdentity()">
                    <select sdInput size="sm" name="ch" aria-label="Channel" [(ngModel)]="newChannel">
                      @for (ch of identityChannels; track ch) {
                        <option [value]="ch">{{ ch }}</option>
                      }
                    </select>
                    <input sdInput size="sm" name="idv" aria-label="Identifier" placeholder="Address, number or id" [(ngModel)]="newIdentifier" />
                    <button sdButton size="sm" type="submit" icon="plus" [disabled]="!newIdentifier.trim()">Add</button>
                  </form>
                }
              </sd-card>
            </div>

            <div class="stack">
              <sd-card title="Consent" subtitle="Per channel and purpose; changes are recorded with their source.">
                <div class="dt-wrap">
                  <table class="dt">
                    <thead>
                      <tr>
                        <th scope="col">Channel</th>
                        @for (p of purposes; track p) {
                          <th scope="col">{{ p }}</th>
                        }
                      </tr>
                    </thead>
                    <tbody>
                      @for (ch of consentChannels; track ch) {
                        <tr>
                          <th scope="row">{{ ch === 'Sms' ? 'SMS' : ch }}</th>
                          @for (p of purposes; track p) {
                            @let row = consent(ch, p);
                            <td>
                              @if (c.permissions.edit) {
                                <select sdInput size="sm" [attr.aria-label]="ch + ' ' + p" [ngModel]="row?.status ?? 'Unknown'" (ngModelChange)="setConsent(ch, p, $event)">
                                  <option value="Unknown">Unknown</option>
                                  <option value="OptedIn">Opted in</option>
                                  <option value="OptedOut">Opted out</option>
                                </select>
                              } @else {
                                {{ row?.status | deskHumanize }}
                              }
                              @if (row?.recordedAt) {
                                <div class="sd-xs sd-muted" [title]="row?.source ?? ''">{{ row!.recordedAt | sdRelativeTime }}</div>
                              }
                            </td>
                          }
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              </sd-card>

              <sd-card title="Customer portal">
                <dl class="meta">
                  <dt>Access</dt>
                  <dd>
                    @if (c.permissions.managePortal) {
                      <select sdInput size="sm" aria-label="Portal access" [ngModel]="c.portalAccess" (ngModelChange)="setPortal($event)">
                        @for (p of portalScopes; track p) {
                          <option [value]="p">{{ portalLabel(p) }}</option>
                        }
                      </select>
                    } @else {
                      {{ portalLabel(c.portalAccess) }}
                    }
                  </dd>
                  <dt>Password</dt>
                  <dd>{{ c.hasPortalPassword ? 'Set' : 'Not set' }}@if (c.portalLocked) { · <sd-badge tone="danger" size="sm">Locked</sd-badge> }</dd>
                  <dt>Last sign-in</dt>
                  <dd>{{ c.portalLastLoginAt ? (c.portalLastLoginAt | sdDateTime) : 'Never' }}</dd>
                </dl>
                @if (c.permissions.managePortal && c.email && c.status === 'Active') {
                  <div class="portal-actions">
                    <button sdButton size="sm" icon="send" [loading]="sending() === 'invite'" (click)="invite('invite')">Send invitation</button>
                    @if (c.portalAccess !== 'None') {
                      <button sdButton size="sm" icon="key" [loading]="sending() === 'reset'" (click)="invite('reset')">Send password reset</button>
                    }
                  </div>
                }
                @if (invitation(); as inv) {
                  <p class="sd-small sent" role="status">
                    {{ inv.mode === 'reset' ? 'Reset code' : 'Invitation' }} sent to {{ inv.sentTo }} (valid until {{ inv.expiresAt | sdDateTime: 'datetime' : 'short' }}).
                    @if (inv.demoCode) {
                      <br />Demo mode code: <strong class="sd-mono">{{ inv.demoCode }}</strong>
                    }
                  </p>
                }
              </sd-card>
            </div>
          </div>

          <div class="section-head">
            <h2>Possible duplicates</h2>
            @if (duplicates().length) {
              <sd-badge tone="warning" size="sm">{{ duplicates().length }}</sd-badge>
            }
          </div>
          @if (duplicates().length === 0) {
            <p class="empty-note">No likely duplicates found.</p>
          } @else {
            <div class="dt-wrap">
              <table class="dt">
                <thead>
                  <tr>
                    <th scope="col">Contact</th>
                    <th scope="col">Customer</th>
                    <th scope="col">Why</th>
                    <th scope="col">Match</th>
                    <th scope="col"><span class="sd-visually-hidden">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (d of duplicates(); track d.contact.id) {
                    <tr>
                      <td>
                        <a [routerLink]="['/customers/contacts', d.contact.id]">{{ d.contact.fullName }}</a>
                        <div class="sd-xs sd-muted">{{ d.contact.email ?? d.contact.phone ?? '' }}</div>
                      </td>
                      <td>{{ d.contact.customerName ?? '—' }}</td>
                      <td class="sd-small">{{ d.reasons.join(' · ') }}</td>
                      <td><sd-badge [tone]="d.confidence === 'High' ? 'danger' : d.confidence === 'Medium' ? 'warning' : 'neutral'" size="sm">{{ (d.score * 100).toFixed(0) }}% · {{ d.confidence }}</sd-badge></td>
                      <td class="row-actions">
                        @if (c.permissions.merge) {
                          <button sdButton size="sm" icon="merge" (click)="merge(d)">Merge…</button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }

          <div class="section-head"><h2>Tickets</h2></div>
          <desk-c360-tickets [contactId]="c.id" />

          @if (history().length) {
            <div class="section-head"><h2>Merge history</h2></div>
            <ul class="history">
              @for (h of history(); track h.id) {
                <li class="sd-small">
                  <sd-icon name="merge" [size]="13" />
                  {{ h.mergedContactName ?? 'A contact' }} merged {{ h.survivorContactId === c.id ? 'into this contact' : 'into another contact' }}
                  by {{ h.mergedByName ?? 'system' }} · {{ h.mergedAt | sdRelativeTime }}
                </li>
              }
            </ul>
          }
        </div>
      }
    </div>
  `,
  styles: [
    TABLE_STYLES,
    SECTION_STYLES,
    `
      .pad {
        padding-top: var(--sd-space-6);
      }
      .grid {
        display: grid;
        gap: var(--sd-space-4);
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr));
      }
      .stack {
        display: grid;
        gap: var(--sd-space-4);
        align-content: start;
      }
      .who {
        display: flex;
        gap: var(--sd-space-3);
        align-items: center;
        margin-bottom: var(--sd-space-3);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        gap: 6px;
        margin-bottom: 4px;
      }
      .cust,
      .merged a {
        color: var(--sd-primary);
      }
      .merged {
        display: flex;
        gap: 8px;
        align-items: center;
        padding: var(--sd-space-2) var(--sd-space-3);
        background: var(--sd-info-bg);
        border: 1px solid var(--sd-info-border);
        border-radius: var(--sd-radius-md);
        margin-bottom: var(--sd-space-4);
      }
      .ids {
        display: grid;
        gap: 6px;
        list-style: none;
        padding: 0;
        margin-bottom: var(--sd-space-3);
      }
      .ids li {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .add-id {
        display: flex;
        gap: 6px;
        flex-wrap: wrap;
      }
      .add-id select {
        width: 120px;
      }
      .add-id input {
        flex: 1;
        min-width: 160px;
      }
      .dt th[scope='row'] {
        background: transparent;
      }
      .portal-actions {
        display: flex;
        gap: var(--sd-space-2);
        flex-wrap: wrap;
        margin-top: var(--sd-space-3);
      }
      .sent {
        margin-top: var(--sd-space-3);
        color: var(--sd-success);
      }
      .history {
        display: grid;
        gap: 6px;
        list-style: none;
        padding: 0;
      }
    `,
  ],
})
export class ContactDetailPage {
  readonly id = input.required<string>();
  private readonly api = inject(ContactsApi);
  private readonly dialog = inject(SdDialogService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  protected readonly purposes = CONSENT_PURPOSES;
  protected readonly consentChannels = CONSENT_CHANNELS;
  protected readonly portalScopes = PORTAL_ACCESS_SCOPES;
  protected readonly identityChannels = ['Email', 'Phone', 'WhatsApp', 'Sms', 'Facebook', 'Instagram', 'Chat'];
  protected readonly contact = signal<ContactDetailDto | null>(null);
  protected readonly duplicates = signal<DuplicateContactCandidateDto[]>([]);
  protected readonly history = signal<ContactMergeLogDto[]>([]);
  protected readonly notFound = signal(false);
  protected readonly sending = signal<'invite' | 'reset' | null>(null);
  protected readonly invitation = signal<PortalInviteResultDto | null>(null);
  protected newChannel = 'Email';
  protected newIdentifier = '';

  protected readonly crumbs = computed<SdBreadcrumb[]>(() => {
    const c = this.contact();
    const list: SdBreadcrumb[] = [{ label: 'Contacts', link: '/customers/contacts' }];
    if (c?.customerId) list.push({ label: c.customerName ?? 'Customer', link: ['/customers', c.customerId] });
    list.push({ label: c?.fullName ?? '…' });
    return list;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.notFound.set(false);
    this.invitation.set(null);
    this.api.get(id).subscribe({
      next: (c) => {
        this.contact.set(c);
        if (c.status === 'Active') this.api.duplicatesOf(id).subscribe((d) => this.duplicates.set(d));
        else this.duplicates.set([]);
        this.api.mergeHistory(id).subscribe((h) => this.history.set(h));
      },
      error: (err: unknown) => {
        if (ApiError.from(err).status === 404) this.notFound.set(true);
      },
    });
  }

  protected consent(channel: string, purpose: string): ContactConsentDto | undefined {
    return this.contact()?.consents.find((c) => c.channel === channel && c.purpose === purpose);
  }

  protected channelIcon(channel: string): string {
    return channel === 'Email' ? 'mail' : channel === 'WhatsApp' || channel === 'Sms' ? 'message-circle' : channel === 'Phone' ? 'phone' : 'at-sign';
  }

  protected portalLabel(p: PortalAccessScope): string {
    return p === 'Own' ? 'Own tickets' : p === 'Location' ? "Location's tickets" : p === 'Organisation' ? "Whole organisation's tickets" : 'No access';
  }

  protected edit(): void {
    const c = this.contact();
    if (!c) return;
    this.dialog
      .open<ContactDetailDto, ContactFormData>(ContactFormDialog, { data: { contact: c }, size: 'lg', disableClose: true })
      .closed.subscribe((saved) => {
        if (!saved) return;
        this.toast.success('Contact updated');
        this.load(c.id);
      });
  }

  protected async remove(): Promise<void> {
    const c = this.contact();
    if (!c) return;
    const ok = await this.dialog.confirm({
      title: `Delete ${c.fullName}?`,
      message: 'The contact is hidden and their identities are released; existing tickets keep the history.',
      danger: true,
      confirmLabel: 'Delete contact',
    });
    if (!ok) return;
    this.api.delete(c.id).subscribe(() => {
      this.toast.success('Contact deleted');
      void this.router.navigate(c.customerId ? ['/customers', c.customerId] : ['/customers/contacts']);
    });
  }

  protected addIdentity(): void {
    const c = this.contact();
    if (!c || !this.newIdentifier.trim()) return;
    this.api.addIdentity(c.id, this.newChannel, this.newIdentifier.trim()).subscribe({
      next: () => {
        this.newIdentifier = '';
        this.load(c.id);
      },
      error: (err: unknown) => this.toast.error('Could not add the identity', saveErrorMessage(err)),
    });
  }

  protected removeIdentity(identityId: string): void {
    const c = this.contact();
    if (!c) return;
    this.api.removeIdentity(c.id, identityId).subscribe(() => this.load(c.id));
  }

  protected setConsent(channel: string, purpose: string, status: string): void {
    const c = this.contact();
    if (!c) return;
    this.api.setConsent(c.id, channel, purpose, status, 'agent update').subscribe((row) =>
      this.contact.update((cur) =>
        cur ? { ...cur, consents: cur.consents.map((x) => (x.channel === channel && x.purpose === purpose ? row : x)) } : cur,
      ),
    );
  }

  protected setPortal(access: PortalAccessScope): void {
    const c = this.contact();
    if (!c) return;
    this.api.setPortalAccess(c.id, access).subscribe({
      next: (updated) => {
        this.contact.set(updated);
        this.toast.success('Portal access updated');
      },
      error: (err: unknown) => {
        this.toast.error('Could not change portal access', saveErrorMessage(err));
        this.load(c.id);
      },
    });
  }

  protected invite(mode: 'invite' | 'reset'): void {
    const c = this.contact();
    if (!c) return;
    this.sending.set(mode);
    this.api.sendPortalInvite(c.id, mode).subscribe({
      next: (r) => {
        this.sending.set(null);
        this.invitation.set(r);
        this.toast.success(mode === 'reset' ? 'Password reset sent' : 'Invitation sent', r.sentTo);
      },
      error: (err: unknown) => {
        this.sending.set(null);
        this.toast.error('Could not send', saveErrorMessage(err));
      },
    });
  }

  protected merge(d: DuplicateContactCandidateDto): void {
    const c = this.contact();
    if (!c) return;
    this.dialog
      .open<ContactMergeResultDto, MergeWizardData>(MergeWizardDialog, {
        data: { firstId: c.id, secondId: d.contact.id, score: d.score, reasons: d.reasons },
        size: 'lg',
      })
      .closed.subscribe((result) => {
        if (!result) return;
        if (result.survivorId !== c.id) void this.router.navigate(['/customers/contacts', result.survivorId]);
        else this.load(c.id);
      });
  }
}

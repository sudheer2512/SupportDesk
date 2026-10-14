import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ConversationDetailDto } from '@sd/core';
import { SdAvatar, SdBadge, SdCard, SdDateTimePipe, SdIcon, SdRelativeTimePipe } from '@sd/ui';
import { channelLabel, conversationStatusLabel, conversationStatusTone, metaString } from '../../shared/channel-ui';

/** Right-hand context of a conversation: customer / visitor, verification, linked ticket, channel and chat details. */
@Component({
  selector: 'desk-conversation-context',
  imports: [RouterLink, SdCard, SdAvatar, SdBadge, SdIcon, SdDateTimePipe, SdRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (detail(); as d) {
      @let s = d.summary;
      <sd-card padding="md" class="block">
        <div class="person">
          <sd-avatar [name]="personName()" size="lg" />
          <div class="sd-truncate">
            <strong class="sd-truncate">{{ personName() }}</strong>
            <div class="sd-small sd-muted sd-truncate">{{ s.contactId ? 'Contact' : s.channel === 'Chat' ? 'Website visitor' : 'Unknown sender' }}</div>
          </div>
        </div>
        <dl class="facts">
          @if (d.visitorEmail) {
            <dt>E-mail</dt>
            <dd class="sd-truncate">{{ d.visitorEmail }}</dd>
          }
          <dt>{{ s.channel === 'Email' ? 'Thread' : s.channel === 'Chat' ? 'Session' : s.channel === 'Phone' ? 'Call id' : 'From' }}</dt>
          <dd class="sd-mono sd-xs sd-truncate" [title]="d.externalKey">{{ d.externalKey }}</dd>
          @if (s.channel === 'Chat') {
            <dt>Identity</dt>
            <dd>
              @if (s.identityVerified) {
                <sd-badge size="sm" tone="success" icon="shield-check">Verified</sd-badge>
              } @else {
                <sd-badge size="sm" tone="warning" icon="shield-alert">Not verified</sd-badge>
              }
            </dd>
            @if (d.rating) {
              <dt>Chat rating</dt>
              <dd>{{ d.rating }} / 5</dd>
            }
          }
        </dl>
      </sd-card>

      <sd-card padding="md" class="block" title="Ticket">
        @if (s.ticketId) {
          <a class="ticket" [routerLink]="['/tickets', s.ticketId]">
            <sd-icon name="ticket" [size]="14" /> #{{ s.ticketNumber }}
            @if (s.ticketStatusCategory) {
              <sd-badge size="sm">{{ s.ticketStatusCategory }}</sd-badge>
            }
          </a>
        } @else {
          <p class="sd-small sd-muted">No ticket yet — a ticket is created when the chat is handed to an agent.</p>
        }
      </sd-card>

      <sd-card padding="md" class="block" title="Conversation">
        <dl class="facts">
          <dt>Status</dt>
          <dd><sd-badge size="sm" [tone]="statusTone(s.status)">{{ statusLabel(s.status) }}</sd-badge></dd>
          <dt>Channel</dt>
          <dd>{{ channelLabel(s.channel) }}</dd>
          @if (s.channelAccountName) {
            <dt>Account</dt>
            <dd class="sd-truncate">{{ s.channelAccountName }}</dd>
          }
          <dt>Assigned</dt>
          <dd>{{ s.assignedUserName ?? '—' }}</dd>
          <dt>Started</dt>
          <dd>{{ s.startedAt | sdDateTime }}</dd>
          <dt>Last activity</dt>
          <dd>{{ s.lastMessageAt | sdRelativeTime }}</dd>
          @if (s.serviceWindowEndsAt) {
            <dt>24h window</dt>
            <dd>{{ d.serviceWindowOpen ? 'Open until ' : 'Closed at ' }}{{ s.serviceWindowEndsAt | sdDateTime }}</dd>
          }
          @if (pageUrl()) {
            <dt>Page</dt>
            <dd class="sd-truncate"><a [href]="pageUrl()" target="_blank" rel="noopener noreferrer">{{ pageUrl() }}</a></dd>
          }
          @if (locale()) {
            <dt>Locale</dt>
            <dd>{{ locale() }}</dd>
          }
        </dl>
      </sd-card>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: var(--sd-space-3);
    }
    .block + .block {
      margin-top: var(--sd-space-3);
      display: block;
    }
    .person {
      display: flex;
      align-items: center;
      gap: var(--sd-space-3);
      margin-bottom: var(--sd-space-3);
      min-width: 0;
    }
    .facts {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 6px var(--sd-space-3);
      margin: 0;
      font-size: var(--sd-text-sm);
    }
    .facts dt {
      color: var(--sd-text-3);
    }
    .facts dd {
      margin: 0;
      min-width: 0;
    }
    .ticket {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-weight: var(--sd-weight-medium);
    }
  `,
})
export class ConversationContextComponent {
  readonly detail = input<ConversationDetailDto | null>(null);

  protected readonly channelLabel = channelLabel;
  protected readonly statusLabel = conversationStatusLabel;
  protected readonly statusTone = conversationStatusTone;
  protected readonly personName = computed(() => {
    const s = this.detail()?.summary;
    return s?.contactName || s?.visitorName || 'Unknown';
  });
  protected readonly pageUrl = computed(() => {
    const url = metaString(this.detail()?.metadata, 'pageUrl');
    return url && /^https?:\/\//i.test(url) ? url : null;
  });
  protected readonly locale = computed(() => metaString(this.detail()?.metadata, 'locale'));
}

import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  AlertController,
  IonBadge,
  IonButton,
  IonContent,
  IonHeader,
  IonLabel,
  IonRefresher,
  IonRefresherContent,
  IonSegment,
  IonSegmentButton,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, ApprovalDto, ApprovalsApi, ClockService, MeStore, formatRelative } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { SessionCoordinator } from '../core/session-coordinator';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { StateView } from '../shared/state-view.component';

type View = 'pending' | 'mine';

const TYPE_LABELS: Record<string, string> = {
  transfer: 'Department transfer',
  blueprint: 'Workflow transition',
  ai_action: 'AI action',
  custom: 'Approval',
};

/** Approvals: requests waiting for my decision (approve / reject with a note) and the ones I raised. */
@Component({
  selector: 'm-agent-approvals',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonRefresher,
    IonRefresherContent,
    IonSegment,
    IonSegmentButton,
    IonLabel,
    IonButton,
    IonBadge,
    OfflineBanner,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar><ion-title>Approvals</ion-title></ion-toolbar>
      <ion-toolbar>
        <ion-segment [value]="view()" (ionChange)="setView($any($event).detail.value)" aria-label="Which approvals">
          <ion-segment-button value="pending"><ion-label>To decide</ion-label></ion-segment-button>
          <ion-segment-button value="mine"><ion-label>Raised by me</ion-label></ion-segment-button>
        </ion-segment>
      </ion-toolbar>
      <m-offline-banner [staleSince]="current().staleSince()" />
    </ion-header>
    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>
      @if (items().length) {
        @for (a of items(); track a.id) {
          <article class="card" [attr.aria-label]="label(a)">
            <header>
              <strong>{{ typeLabel(a.type) }}</strong>
              <ion-badge [color]="a.status === 'Pending' ? 'warning' : a.status === 'Approved' ? 'success' : 'medium'">{{ a.status }}</ion-badge>
            </header>
            @if (a.ticketId) {
              <a class="ticket" [routerLink]="['/agent/tickets', a.ticketId]">#{{ a.ticketNumber }} · {{ a.ticketSubject }}</a>
            }
            @if (a.reason) {
              <p class="reason">“{{ a.reason }}”</p>
            }
            <p class="meta">Requested by {{ a.requestedByName }} · {{ rel(a.createdAt) }}{{ a.expiresAt ? ' · expires ' + rel(a.expiresAt) : '' }}</p>
            @if (a.decidedByName) {
              <p class="meta">{{ a.status }} by {{ a.decidedByName }}{{ a.decisionNote ? ': ' + a.decisionNote : '' }}</p>
            }
            @if (view() === 'pending' && a.status === 'Pending') {
              <div class="actions">
                <ion-button color="success" [disabled]="readOnly() || busy() === a.id" (click)="decide(a, true)">Approve</ion-button>
                <ion-button color="danger" fill="outline" [disabled]="readOnly() || busy() === a.id" (click)="decide(a, false)">Reject</ion-button>
              </div>
            }
          </article>
        }
      } @else if (current().loading()) {
        <m-state kind="loading" title="Loading approvals" />
      } @else if (current().error()) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [message]="current().error()!.userMessage" actionLabel="Try again" (action)="current().load()" />
      } @else {
        <m-state kind="empty" [title]="view() === 'pending' ? 'Nothing to decide' : 'No requests'"
          [message]="view() === 'pending' ? (canDecide() ? 'Transfer and workflow approvals addressed to you appear here.' : 'You can see approvals addressed to you here.') : 'Approvals you request appear here.'" />
      }
    </ion-content>
  `,
  styles: `
    .card {
      margin: 12px;
      padding: 12px 14px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg, 12px);
      background: var(--sd-surface);
    }
    header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
    }
    .ticket {
      display: block;
      margin-top: 6px;
      font-weight: 600;
      color: var(--ion-color-primary);
      text-decoration: none;
      min-height: 32px;
    }
    .reason {
      margin: 6px 0;
      font-style: italic;
    }
    .meta {
      margin: 2px 0;
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    .actions {
      display: flex;
      gap: 8px;
      margin-top: 8px;
    }
    .actions ion-button {
      flex: 1;
    }
  `,
})
export class AgentApprovalsPage implements OnInit {
  private readonly api = inject(ApprovalsApi);
  private readonly me = inject(MeStore);
  private readonly session = inject(SessionCoordinator);
  private readonly clock = inject(ClockService);
  private readonly alerts = inject(AlertController);
  private readonly toasts = inject(MobileToasts);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly view = signal<View>('pending');
  protected readonly busy = signal<string | null>(null);
  protected readonly readOnly = this.session.readOnly;
  protected readonly canDecide = computed(() => this.me.can('approvals.decide'));

  private readonly pending = cachedResource<ApprovalDto[]>({ key: () => 'approvals:pending', kind: 'meta', fetch: () => this.api.list('Pending') });
  private readonly mine = cachedResource<ApprovalDto[]>({ key: () => 'approvals:mine', kind: 'meta', fetch: () => this.api.list(null, true) });
  protected readonly current = computed(() => (this.view() === 'pending' ? this.pending : this.mine));
  protected readonly items = computed(() => this.current().value() ?? []);

  ngOnInit(): void {
    void this.pending.load();
    const sub = this.session.refresh$.subscribe(() => void this.current().load());
    this.destroyRef.onDestroy(() => sub.unsubscribe());
  }

  protected setView(view: View): void {
    this.view.set(view === 'mine' ? 'mine' : 'pending');
    void this.current().load();
  }

  protected async refresh(event: Event): Promise<void> {
    await this.current().load();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected async decide(a: ApprovalDto, approve: boolean): Promise<void> {
    const alert = await this.alerts.create({
      header: approve ? 'Approve request?' : 'Reject request?',
      subHeader: a.ticketNumber ? `#${a.ticketNumber} · ${this.typeLabel(a.type)}` : this.typeLabel(a.type),
      inputs: [{ name: 'note', type: 'textarea', placeholder: approve ? 'Note (optional)' : 'Why? (shared with the requester)', attributes: { 'aria-label': 'Decision note' } }],
      buttons: [
        { text: 'Cancel', role: 'cancel' },
        { text: approve ? 'Approve' : 'Reject', role: 'confirm' },
      ],
    });
    await alert.present();
    const { role, data } = await alert.onDidDismiss<{ values: { note?: string } }>();
    if (role !== 'confirm') return;
    this.busy.set(a.id);
    try {
      const updated = await firstValueFrom(this.api.decide(a.id, { approve, note: data?.values?.note?.trim() || null }));
      this.pending.set((this.pending.value() ?? []).map((x) => (x.id === a.id ? updated : x)).filter((x) => x.status === 'Pending'));
      void this.toasts.success(approve ? 'Approved.' : 'Rejected.');
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
      void this.pending.load();
    } finally {
      this.busy.set(null);
    }
  }

  protected typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type.replace(/_/g, ' ');
  }

  protected label(a: ApprovalDto): string {
    return `${this.typeLabel(a.type)}${a.ticketNumber ? ' for ticket ' + a.ticketNumber : ''}, ${a.status}`;
  }

  protected rel(value: string): string {
    return formatRelative(value, this.clock.coarseNow());
  }
}

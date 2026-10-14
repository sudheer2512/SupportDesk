import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  AlertController,
  IonButton,
  IonContent,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonNote,
  IonSegment,
  IonSegmentButton,
  IonTitle,
  IonToggle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { AVAILABILITIES, ApiError, Availability, MeStore, PortalAuthService, ThemePreference } from '@sd/core';
import { environment } from '../../environments/environment';
import { AvailabilityStore } from '../agent/agent-stores';
import { ModeService } from '../core/mobile-session';
import { OutboxService } from '../core/outbox.service';
import { PushRegistrar } from '../core/push-registrar';
import { SessionCoordinator } from '../core/session-coordinator';
import { SessionSnapshotStore } from '../core/session-snapshot';
import { MobileThemeService, MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { OutboxItem } from '../shared/outbox-item.component';

/** Who is signed in, availability (agents), appearance, push notifications, unsent messages and sign-out. */
@Component({
  selector: 'm-profile-page',
  imports: [
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonList,
    IonListHeader,
    IonItem,
    IonLabel,
    IonNote,
    IonButton,
    IonIcon,
    IonSegment,
    IonSegmentButton,
    IonToggle,
    OfflineBanner,
    OutboxItem,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar><ion-title>Me</ion-title></ion-toolbar>
      <m-offline-banner />
    </ion-header>
    <ion-content>
      <ion-list lines="full">
        <ion-item>
          <ion-icon slot="start" name="person-circle-outline" class="avatar" aria-hidden="true" />
          <ion-label>
            <h2>{{ name() }}</h2>
            <p>{{ detail() }}</p>
          </ion-label>
        </ion-item>
      </ion-list>

      @if (isAgent()) {
        <ion-list lines="none">
          <ion-list-header><ion-label>Availability</ion-label></ion-list-header>
          <ion-item>
            <ion-segment [value]="availability()" [disabled]="readOnly()" (ionChange)="setAvailability($any($event).detail.value)" aria-label="Availability">
              @for (a of availabilities; track a) {
                <ion-segment-button [value]="a"><ion-label>{{ a }}</ion-label></ion-segment-button>
              }
            </ion-segment>
          </ion-item>
          <ion-note class="hint">New tickets are routed to you only while you are Online.</ion-note>
        </ion-list>
      }

      <ion-list lines="none">
        <ion-list-header><ion-label>Appearance</ion-label></ion-list-header>
        <ion-item>
          <ion-segment [value]="theme.theme.preference()" (ionChange)="setTheme($any($event).detail.value)" aria-label="Theme">
            <ion-segment-button value="system"><ion-label>System</ion-label></ion-segment-button>
            <ion-segment-button value="light"><ion-label>Light</ion-label></ion-segment-button>
            <ion-segment-button value="dark"><ion-label>Dark</ion-label></ion-segment-button>
          </ion-segment>
        </ion-item>
      </ion-list>

      <ion-list lines="full">
        <ion-list-header><ion-label>Notifications</ion-label></ion-list-header>
        @if (push.supported) {
          <ion-item>
            <ion-toggle [checked]="pushOn()" [disabled]="readOnly() || pushBusy()" (ionChange)="togglePush($any($event).detail.checked)">
              Push notifications on this device
            </ion-toggle>
          </ion-item>
          @if (push.lastError()) {
            <ion-note color="danger" class="hint">{{ push.lastError() }}</ion-note>
          }
        } @else {
          <ion-item>
            <ion-label class="ion-text-wrap">
              <h3>Push notifications</h3>
              <p>Not available in this build. {{ isAgent() ? 'Alerts still arrive live in the Alerts tab.' : 'Updates still arrive live while the app is open.' }}</p>
            </ion-label>
          </ion-item>
        }
      </ion-list>

      <ion-list lines="none">
        <ion-list-header><ion-label>Unsent messages ({{ outbox.pendingCount() }})</ion-label></ion-list-header>
        @for (e of outbox.entries(); track e.id) {
          <m-outbox-item [entry]="e" [showLabel]="true" [editable]="false" />
        } @empty {
          <ion-note class="hint">Everything you wrote has been delivered.</ion-note>
        }
      </ion-list>

      <div class="ion-padding">
        <ion-button expand="block" color="danger" fill="outline" (click)="signOut()">
          <ion-icon slot="start" name="log-out-outline" aria-hidden="true" />Sign out
        </ion-button>
        <p class="version">SupportDesk mobile {{ version }} · {{ isAgent() ? 'Agent' : 'Customer' }} mode</p>
      </div>
    </ion-content>
  `,
  styles: `
    .avatar {
      font-size: 40px;
      color: var(--ion-color-primary);
    }
    .hint {
      display: block;
      padding: 4px 16px 8px;
      font-size: 0.8rem;
    }
    ion-segment {
      width: 100%;
    }
    .version {
      margin-top: 16px;
      text-align: center;
      font-size: 0.75rem;
      color: var(--sd-text-2);
    }
  `,
})
export class ProfilePage {
  private readonly mode = inject(ModeService);
  private readonly me = inject(MeStore);
  private readonly snapshot = inject(SessionSnapshotStore);
  private readonly customer = inject(PortalAuthService);
  private readonly availabilityStore = inject(AvailabilityStore);
  private readonly session = inject(SessionCoordinator);
  private readonly alerts = inject(AlertController);
  private readonly toasts = inject(MobileToasts);
  protected readonly theme = inject(MobileThemeService);
  protected readonly push = inject(PushRegistrar);
  protected readonly outbox = inject(OutboxService);

  protected readonly availabilities = AVAILABILITIES;
  protected readonly version = environment.appVersion;
  protected readonly readOnly = this.session.readOnly;
  protected readonly isAgent = computed(() => this.mode.mode() === 'agent');
  protected readonly availability = this.availabilityStore.current;
  protected readonly pushBusy = signal(false);
  protected readonly pushOn = computed(() => this.push.permission() === 'granted' && this.push.registeredOnServer());

  protected readonly name = computed(() => {
    const s = this.snapshot.current();
    if (this.isAgent()) return this.me.displayName() || (s?.kind === 'agent' ? s.displayName : '');
    return this.customer.contact()?.fullName ?? (s?.kind === 'customer' ? s.fullName : '');
  });
  protected readonly detail = computed(() => {
    const s = this.snapshot.current();
    if (this.isAgent()) {
      const u = this.me.user();
      if (u) return `${u.email} · ${u.tenantName}`;
      return s?.kind === 'agent' ? `${s.email} · ${s.tenantName}` : '';
    }
    const c = this.customer.contact();
    const email = c?.email ?? (s?.kind === 'customer' ? s.email : null);
    const org = c?.customerName ?? (s?.kind === 'customer' ? s.customerName : null);
    return [email, org, this.mode.brand() ? `${this.mode.brand()} help center` : null].filter(Boolean).join(' · ');
  });

  protected async setAvailability(value: Availability): Promise<void> {
    if (value === this.availability()) return;
    try {
      await this.availabilityStore.set(value);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected setTheme(value: ThemePreference): void {
    this.theme.theme.set(value === 'light' || value === 'dark' ? value : 'system');
  }

  protected async togglePush(on: boolean): Promise<void> {
    this.pushBusy.set(true);
    try {
      if (on) {
        if (await this.push.enable()) void this.toasts.success('Push notifications are on for this device.');
      } else {
        await this.push.disable();
      }
    } finally {
      this.pushBusy.set(false);
    }
  }

  protected async signOut(): Promise<void> {
    const pending = this.outbox.pendingCount();
    if (pending > 0 || this.session.offlineSession()) {
      const alert = await this.alerts.create({
        header: 'Sign out?',
        message: pending
          ? `${pending} unsent message${pending === 1 ? '' : 's'} will be deleted from this device.`
          : 'You are offline. Signing out removes the saved data from this device.',
        buttons: [
          { text: 'Cancel', role: 'cancel' },
          { text: 'Sign out', role: 'destructive' },
        ],
      });
      await alert.present();
      if ((await alert.onDidDismiss()).role !== 'destructive') return;
    }
    await this.session.signOut();
  }
}

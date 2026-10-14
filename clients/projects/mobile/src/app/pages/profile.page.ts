import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import { IonButton, IonContent, IonHeader, IonIcon, IonItem, IonLabel, IonList, IonTitle, IonToolbar } from '@ionic/angular/standalone';
import { AuthService, MeStore, PortalAuthService } from '@sd/core';
import { ModeService } from '../core/mobile-session';

/** Who is signed in (agent or customer) and sign-out. */
@Component({
  selector: 'm-profile-page',
  imports: [IonHeader, IonToolbar, IonTitle, IonContent, IonList, IonItem, IonLabel, IonButton, IonIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>Profile</ion-title>
      </ion-toolbar>
    </ion-header>
    <ion-content>
      <ion-list lines="full">
        <ion-item>
          <ion-label>
            <h2>{{ name() }}</h2>
            <p>{{ detail() }}</p>
          </ion-label>
        </ion-item>
      </ion-list>
      <div class="ion-padding">
        <ion-button expand="block" color="danger" fill="outline" (click)="signOut()">
          <ion-icon slot="start" name="log-out-outline" aria-hidden="true" />Sign out
        </ion-button>
      </div>
    </ion-content>
  `,
})
export class ProfilePage {
  private readonly mode = inject(ModeService);
  private readonly me = inject(MeStore);
  private readonly agent = inject(AuthService);
  private readonly customer = inject(PortalAuthService);
  private readonly router = inject(Router);

  protected readonly name = computed(() =>
    this.mode.mode() === 'agent' ? this.me.displayName() : (this.customer.contact()?.fullName ?? ''),
  );
  protected readonly detail = computed(() => {
    if (this.mode.mode() === 'agent') {
      const u = this.me.user();
      return u ? `${u.email} · ${u.tenantName}` : '';
    }
    const c = this.customer.contact();
    return c ? `${c.email ?? ''}${c.customerName ? ' · ' + c.customerName : ''}` : '';
  });

  protected async signOut(): Promise<void> {
    if (this.mode.mode() === 'agent') {
      await this.agent.logout();
    } else {
      await this.customer.logout();
      await this.router.navigateByUrl('/login', { replaceUrl: true });
    }
  }
}

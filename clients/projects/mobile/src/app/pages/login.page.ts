import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  IonButton,
  IonContent,
  IonHeader,
  IonInput,
  IonItem,
  IonLabel,
  IonList,
  IonNote,
  IonSegment,
  IonSegmentButton,
  IonSpinner,
  IonText,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, AuthService, LoginMfaRequiredResponse, PortalAuthService } from '@sd/core';
import { AppMode, ModeService } from '../core/mobile-session';

/** Sign-in for agents (workspace + e-mail + password, MFA code step) or customers (portal + e-mail + password). */
@Component({
  selector: 'm-login-page',
  imports: [
    FormsModule,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonSegment,
    IonSegmentButton,
    IonLabel,
    IonList,
    IonItem,
    IonInput,
    IonButton,
    IonNote,
    IonText,
    IonSpinner,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-title>SupportDesk</ion-title>
      </ion-toolbar>
    </ion-header>
    <ion-content class="ion-padding">
      <ion-segment [value]="mode.mode()" (ionChange)="switchMode($any($event).detail.value)">
        <ion-segment-button value="agent"><ion-label>Agent</ion-label></ion-segment-button>
        <ion-segment-button value="customer"><ion-label>Customer</ion-label></ion-segment-button>
      </ion-segment>

      @if (error()) {
        <ion-text color="danger"><p role="alert">{{ error() }}</p></ion-text>
      }

      @if (mode.mode() === 'agent') {
        @if (!challenge()) {
          <form (ngSubmit)="agentLogin()">
            <ion-list lines="full">
              <ion-item>
                <ion-input label="Workspace" labelPlacement="stacked" name="tenant" autocapitalize="off" [(ngModel)]="tenant" />
              </ion-item>
              <ion-item>
                <ion-input label="Work e-mail" labelPlacement="stacked" type="email" name="email" autocomplete="username" [(ngModel)]="email" />
              </ion-item>
              <ion-item>
                <ion-input label="Password" labelPlacement="stacked" type="password" name="password" autocomplete="current-password" [(ngModel)]="password" />
              </ion-item>
            </ion-list>
            <ion-button expand="block" type="submit" [disabled]="busy()">
              @if (busy()) {
                <ion-spinner name="crescent" />
              } @else {
                Sign in
              }
            </ion-button>
          </form>
        } @else {
          <form (ngSubmit)="agentMfa()">
            <p>Enter the verification code from your authenticator app or e-mail.</p>
            @if (challenge()?.demoCode) {
              <ion-note color="secondary"><strong>Demo environment</strong> — code {{ challenge()?.demoCode }}</ion-note>
            }
            <ion-list lines="full">
              <ion-item>
                <ion-input label="Code" labelPlacement="stacked" inputmode="numeric" name="code" autocomplete="one-time-code" [(ngModel)]="code" />
              </ion-item>
            </ion-list>
            <ion-button expand="block" type="submit" [disabled]="busy()">Verify</ion-button>
            <ion-button expand="block" fill="clear" type="button" (click)="challenge.set(null)">Back</ion-button>
          </form>
        }
      } @else {
        <form (ngSubmit)="customerLogin()">
          <ion-list lines="full">
            <ion-item>
              <ion-input label="Support portal" labelPlacement="stacked" name="brand" autocapitalize="off" [(ngModel)]="brand" />
            </ion-item>
            <ion-item>
              <ion-input label="E-mail" labelPlacement="stacked" type="email" name="cemail" autocomplete="username" [(ngModel)]="email" />
            </ion-item>
            <ion-item>
              <ion-input label="Password" labelPlacement="stacked" type="password" name="cpassword" autocomplete="current-password" [(ngModel)]="password" />
            </ion-item>
          </ion-list>
          <ion-button expand="block" type="submit" [disabled]="busy()">Sign in</ion-button>
        </form>
      }
    </ion-content>
  `,
  styles: `
    ion-segment {
      margin-bottom: 16px;
    }
    form {
      margin-top: 8px;
    }
    ion-list {
      margin-bottom: 16px;
    }
  `,
})
export class LoginPage {
  protected readonly mode = inject(ModeService);
  private readonly agentAuth = inject(AuthService);
  private readonly customerAuth = inject(PortalAuthService);
  private readonly router = inject(Router);

  protected tenant = this.agentAuth.rememberedLogin().tenant;
  protected email = this.agentAuth.rememberedLogin().email;
  protected password = '';
  protected code = '';
  protected brand = this.mode.brand();
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly challenge = signal<LoginMfaRequiredResponse | null>(null);

  protected switchMode(value: AppMode): void {
    this.mode.setMode(value === 'customer' ? 'customer' : 'agent');
    this.error.set(null);
    this.challenge.set(null);
  }

  protected agentLogin(): Promise<void> {
    return this.run(async () => {
      const res = await this.agentAuth.login(this.tenant, this.email, this.password);
      if (res.status === 'mfa_required') this.challenge.set(res);
      else if (res.status === 'password_expired') this.error.set(res.message ?? 'Your password has expired. Ask an administrator to reset it.');
      else await this.router.navigateByUrl('/agent/queue', { replaceUrl: true });
    });
  }

  protected agentMfa(): Promise<void> {
    const c = this.challenge();
    if (!c) return Promise.resolve();
    return this.run(async () => {
      const res = await this.agentAuth.verifyMfa(c.challengeId, this.code, c.methods.includes('totp') ? 'totp' : (c.methods[0] ?? 'totp'));
      if (res.status === 'ok') await this.router.navigateByUrl('/agent/queue', { replaceUrl: true });
    });
  }

  protected customerLogin(): Promise<void> {
    return this.run(async () => {
      this.mode.setBrand(this.brand);
      const res = await this.customerAuth.login(this.mode.brand(), this.email, this.password);
      if (res.status === 'ok') await this.router.navigateByUrl('/customer/tickets', { replaceUrl: true });
      else this.error.set(res.message ?? 'Sign-in could not be completed.');
    });
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (err) {
      const e = ApiError.from(err);
      this.error.set(e.status === 401 ? 'The details you entered are not correct.' : e.userMessage);
    } finally {
      this.busy.set(false);
    }
  }
}

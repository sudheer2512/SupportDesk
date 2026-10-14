import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
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
import { NetworkService } from '../core/network.service';

type CustomerMethod = 'password' | 'code';

/**
 * One app, two modes chosen here: agents sign in with workspace + e-mail + password (+ MFA code); customers with the
 * help-center name + e-mail and either a password or a one-time code sent by e-mail.
 */
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
      <div class="wrap">
        <p class="lead">{{ mode.mode() === 'agent' ? 'Work your queue on the go.' : 'Get help and track your requests.' }}</p>
        <ion-segment [value]="mode.mode()" (ionChange)="switchMode($any($event).detail.value)" aria-label="I am">
          <ion-segment-button value="agent"><ion-label>Agent</ion-label></ion-segment-button>
          <ion-segment-button value="customer"><ion-label>Customer</ion-label></ion-segment-button>
        </ion-segment>

        @if (!online()) {
          <ion-note color="warning" class="block" role="status">You are offline. Signing in needs a connection.</ion-note>
        }
        @if (error()) {
          <ion-text color="danger"><p role="alert">{{ error() }}</p></ion-text>
        }
        @if (info()) {
          <ion-text color="success"><p role="status">{{ info() }}</p></ion-text>
        }

        @if (mode.mode() === 'agent') {
          @if (!challenge()) {
            <form (ngSubmit)="agentLogin()">
              <ion-list lines="full">
                <ion-item>
                  <ion-input label="Workspace" labelPlacement="stacked" name="tenant" autocapitalize="off" autocomplete="organization" [(ngModel)]="tenant" [required]="true" />
                </ion-item>
                <ion-item>
                  <ion-input label="Work e-mail" labelPlacement="stacked" type="email" name="email" autocomplete="username" inputmode="email" [(ngModel)]="email" [required]="true" />
                </ion-item>
                <ion-item>
                  <ion-input label="Password" labelPlacement="stacked" type="password" name="password" autocomplete="current-password" [(ngModel)]="password" [required]="true" />
                </ion-item>
              </ion-list>
              <ion-button expand="block" type="submit" [disabled]="busy() || !online()">
                @if (busy()) {
                  <ion-spinner name="crescent" aria-label="Signing in" />
                } @else {
                  Sign in
                }
              </ion-button>
            </form>
          } @else {
            <form (ngSubmit)="agentMfa()">
              <p>Enter the verification code from your authenticator app or e-mail.</p>
              @if (challenge()?.demoCode) {
                <ion-note color="secondary" class="block"><strong>Demo environment</strong> — code {{ challenge()?.demoCode }}</ion-note>
              }
              <ion-list lines="full">
                <ion-item>
                  <ion-input label="Code" labelPlacement="stacked" inputmode="numeric" name="code" autocomplete="one-time-code" [(ngModel)]="code" [required]="true" />
                </ion-item>
              </ion-list>
              <ion-button expand="block" type="submit" [disabled]="busy()">Verify</ion-button>
              <ion-button expand="block" fill="clear" type="button" (click)="challenge.set(null)">Back</ion-button>
            </form>
          }
        } @else {
          <form (ngSubmit)="customerSubmit()">
            <ion-list lines="full">
              <ion-item>
                <ion-input label="Help center" labelPlacement="stacked" name="brand" autocapitalize="off" helperText="The name in your support portal address, e.g. acme"
                  [(ngModel)]="brand" [required]="true" />
              </ion-item>
              <ion-item>
                <ion-input label="E-mail" labelPlacement="stacked" type="email" name="cemail" autocomplete="username" inputmode="email" [(ngModel)]="email" [required]="true" />
              </ion-item>
            </ion-list>
            <ion-segment [value]="method()" (ionChange)="setMethod($any($event).detail.value)" aria-label="Sign-in method" class="method">
              <ion-segment-button value="password"><ion-label>Password</ion-label></ion-segment-button>
              <ion-segment-button value="code"><ion-label>E-mail me a code</ion-label></ion-segment-button>
            </ion-segment>
            @if (method() === 'password') {
              <ion-list lines="full">
                <ion-item>
                  <ion-input label="Password" labelPlacement="stacked" type="password" name="cpassword" autocomplete="current-password" [(ngModel)]="password" />
                </ion-item>
              </ion-list>
            } @else if (codeSent()) {
              @if (demoCode()) {
                <ion-note color="secondary" class="block"><strong>Demo environment</strong> — code {{ demoCode() }}</ion-note>
              }
              <ion-list lines="full">
                <ion-item>
                  <ion-input label="Code from the e-mail" labelPlacement="stacked" inputmode="numeric" name="otp" autocomplete="one-time-code" [(ngModel)]="code" />
                </ion-item>
              </ion-list>
            }
            <ion-button expand="block" type="submit" [disabled]="busy() || !online()">
              @if (busy()) {
                <ion-spinner name="crescent" aria-label="Please wait" />
              } @else {
                {{ customerAction() }}
              }
            </ion-button>
            @if (method() === 'code' && codeSent()) {
              <ion-button expand="block" fill="clear" type="button" [disabled]="busy()" (click)="requestCode()">Send a new code</ion-button>
            }
          </form>
        }
      </div>
    </ion-content>
  `,
  styles: `
    .wrap {
      max-width: 480px;
      margin: 0 auto;
    }
    .lead {
      margin: 8px 0 16px;
      color: var(--sd-text-2);
    }
    ion-segment {
      margin-bottom: 16px;
    }
    .method {
      margin: 16px 0 8px;
    }
    form {
      margin-top: 8px;
    }
    ion-list {
      margin-bottom: 16px;
    }
    .block {
      display: block;
      margin: 8px 0;
    }
  `,
})
export class LoginPage {
  protected readonly mode = inject(ModeService);
  private readonly agentAuth = inject(AuthService);
  private readonly customerAuth = inject(PortalAuthService);
  private readonly router = inject(Router);
  protected readonly online = inject(NetworkService).online;

  protected tenant = this.agentAuth.rememberedLogin().tenant;
  protected email = this.mode.mode() === 'customer' && this.mode.brand() ? this.customerAuth.rememberedEmail(this.mode.brand()) : this.agentAuth.rememberedLogin().email;
  protected password = '';
  protected code = '';
  protected brand = this.mode.brand();
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly info = signal<string | null>(null);
  protected readonly challenge = signal<LoginMfaRequiredResponse | null>(null);
  protected readonly method = signal<CustomerMethod>('password');
  protected readonly codeSent = signal(false);
  protected readonly demoCode = signal<string | null>(null);
  protected readonly customerAction = computed(() =>
    this.method() === 'password' ? 'Sign in' : this.codeSent() ? 'Verify code' : 'Send me a code',
  );

  protected switchMode(value: AppMode): void {
    this.mode.setMode(value === 'customer' ? 'customer' : 'agent');
    this.reset();
  }

  protected setMethod(value: CustomerMethod): void {
    this.method.set(value === 'code' ? 'code' : 'password');
    this.reset();
  }

  protected agentLogin(): Promise<void> {
    return this.run(async () => {
      const res = await this.agentAuth.login(this.tenant, this.email, this.password);
      if (res.status === 'mfa_required') this.challenge.set(res);
      else if (res.status === 'password_expired') this.error.set(res.message ?? 'Your password has expired. Ask an administrator to reset it.');
      else await this.router.navigateByUrl('/agent/home', { replaceUrl: true });
    });
  }

  protected agentMfa(): Promise<void> {
    const c = this.challenge();
    if (!c) return Promise.resolve();
    return this.run(async () => {
      const res = await this.agentAuth.verifyMfa(c.challengeId, this.code, c.methods.includes('totp') ? 'totp' : (c.methods[0] ?? 'totp'));
      if (res.status === 'ok') await this.router.navigateByUrl('/agent/home', { replaceUrl: true });
      else this.error.set(res.message ?? 'The code is not correct.');
    });
  }

  protected customerSubmit(): Promise<void> {
    if (this.method() === 'password') return this.customerLogin();
    return this.codeSent() ? this.verifyCode() : this.requestCode();
  }

  protected requestCode(): Promise<void> {
    return this.run(async () => {
      this.mode.setBrand(this.brand);
      const res = await this.customerAuth.requestOtp(this.mode.brand(), this.email);
      if (res.status === 'otp_sent' || res.status === 'ok') {
        this.codeSent.set(true);
        this.demoCode.set(res.demoCode ?? null);
        this.info.set(res.message ?? 'If the address has portal access, a code is on its way.');
      } else {
        this.error.set(res.message ?? 'A code could not be sent.');
      }
    });
  }

  private verifyCode(): Promise<void> {
    return this.run(async () => {
      const res = await this.customerAuth.verifyOtp(this.mode.brand(), this.email, this.code);
      if (res.status === 'ok') await this.router.navigateByUrl('/customer/tickets', { replaceUrl: true });
      else this.error.set(res.message ?? 'The code is not correct or has expired.');
    });
  }

  private customerLogin(): Promise<void> {
    return this.run(async () => {
      this.mode.setBrand(this.brand);
      const res = await this.customerAuth.login(this.mode.brand(), this.email, this.password);
      if (res.status === 'ok') await this.router.navigateByUrl('/customer/tickets', { replaceUrl: true });
      else this.error.set(res.message ?? 'Sign-in could not be completed.');
    });
  }

  private reset(): void {
    this.error.set(null);
    this.info.set(null);
    this.challenge.set(null);
    this.codeSent.set(false);
    this.demoCode.set(null);
    this.code = '';
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await action();
    } catch (err) {
      const e = ApiError.from(err);
      this.error.set(e.status === 401 ? 'The details you entered are not correct.' : e.status === 404 ? 'That help center or workspace does not exist.' : e.userMessage);
    } finally {
      this.busy.set(false);
    }
  }
}

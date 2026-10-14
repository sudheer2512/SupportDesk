import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { IonBackButton, IonButtons, IonContent, IonHeader, IonTitle, IonToolbar } from '@ionic/angular/standalone';
import { ApiError, TicketsApi } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { StateView } from '../shared/state-view.component';

/** Deep links by ticket number (`/agent/tickets/number/1042`): resolves the id, then opens the ticket screen. */
@Component({
  selector: 'm-ticket-by-number',
  imports: [IonHeader, IonToolbar, IonTitle, IonButtons, IonBackButton, IonContent, StateView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-buttons slot="start"><ion-back-button defaultHref="/agent/tickets" text="" aria-label="Back" /></ion-buttons>
        <ion-title>Ticket {{ number() }}</ion-title>
      </ion-toolbar>
    </ion-header>
    <ion-content>
      @if (error()) {
        <m-state kind="error" [message]="error()!" actionLabel="Try again" (action)="resolve()" />
      } @else {
        <m-state kind="loading" [lines]="2" title="Opening ticket" />
      }
    </ion-content>
  `,
})
export class TicketByNumberPage implements OnInit {
  readonly number = input.required<string>();
  private readonly api = inject(TicketsApi);
  private readonly router = inject(Router);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    void this.resolve();
  }

  protected async resolve(): Promise<void> {
    this.error.set(null);
    try {
      const t = await firstValueFrom(this.api.getByNumber(this.number()));
      await this.router.navigate(['/agent/tickets', t.id], { replaceUrl: true });
    } catch (e) {
      const err = ApiError.from(e);
      this.error.set(err.status === 404 ? `Ticket ${this.number()} does not exist or is outside your queues.` : err.userMessage);
    }
  }
}

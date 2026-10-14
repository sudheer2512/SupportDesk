import { ChangeDetectionStrategy, Component, ElementRef, Injector, afterNextRender, inject, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PortalAuthService, PortalSubmissionResultDto, PortalTicketCreatedDto } from '@sd/core';
import { SdButton, SdIcon } from '@sd/ui';
import { BrandService } from '../../brand.service';
import { BreadcrumbComponent } from '../../shared/breadcrumb.component';
import { AnonymousFormComponent } from './anonymous-form.component';
import { TicketFormComponent } from './ticket-form.component';

type Done = { kind: 'ticket'; ticket: PortalTicketCreatedDto } | { kind: 'anonymous'; result: PortalSubmissionResultDto };

/**
 * Submit a ticket: the brand's dynamic form for signed-in contacts, the anonymous form otherwise (when
 * the brand allows it — the route guard sends everyone else to sign in), then a confirmation that links
 * to the new ticket.
 */
@Component({
  selector: 'portal-submit-page',
  imports: [RouterLink, SdButton, SdIcon, BreadcrumbComponent, TicketFormComponent, AnonymousFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="portal-container portal-page narrow">
      <portal-breadcrumb [items]="[{ label: 'Home', link: brand.base() }, { label: 'Submit a ticket' }]" />
      @if (done(); as d) {
        <section class="done" aria-labelledby="done-title">
          <span class="done-ic" aria-hidden="true"><sd-icon name="ticket-check" [size]="30" /></span>
          @if (d.kind === 'ticket') {
            <h1 id="done-title" #doneHeading tabindex="-1">Ticket {{ d.ticket.displayNumber }} submitted</h1>
            <p>Thanks — we've received your request. You'll get updates by e-mail and can follow the conversation under My tickets.</p>
            <div class="actions">
              <a sdButton variant="primary" icon="ticket" [routerLink]="[brand.base(), 'tickets', d.ticket.id]">View your ticket</a>
              <button sdButton type="button" icon="file-plus" (click)="reset()">Submit another</button>
              <a sdButton variant="ghost" [routerLink]="brand.base()">Back to the help center</a>
            </div>
          } @else {
            <h1 id="done-title" #doneHeading tabindex="-1">
              {{ d.result.displayNumber ? 'Request ' + d.result.displayNumber + ' received' : 'Request received' }}
            </h1>
            <p>{{ d.result.message }}</p>
            <div class="actions">
              <a sdButton variant="primary" [routerLink]="brand.base()">Back to the help center</a>
              <button sdButton type="button" icon="file-plus" (click)="reset()">Submit another</button>
            </div>
          }
        </section>
      } @else {
        <header class="portal-page-head">
          <div>
            <h1>Submit a ticket</h1>
            <p class="portal-sub">Tell us what you need help with and our team will get back to you.</p>
          </div>
        </header>
        <div class="card">
          @if (auth.isAuthenticated()) {
            <portal-ticket-form (created)="finish({ kind: 'ticket', ticket: $event })" />
          } @else if (brand.features().anonymousTickets) {
            <portal-anonymous-form (submitted)="finish({ kind: 'anonymous', result: $event })" />
          } @else {
            <p class="portal-callout portal-callout--info" role="note">
              <sd-icon name="info" [size]="16" />
              <span>Please <a [routerLink]="brand.base() + '/login'" [queryParams]="{ returnUrl: brand.base() + '/submit' }">sign in</a> to submit a ticket.</span>
            </p>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .narrow {
      max-width: 860px;
    }
    .card {
      position: relative;
      padding: clamp(16px, 4vw, 26px);
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      box-shadow: var(--sd-shadow-sm);
    }
    .done {
      display: grid;
      justify-items: center;
      gap: 10px;
      margin-top: 24px;
      padding: clamp(24px, 6vw, 48px) clamp(16px, 4vw, 32px);
      border: 1px solid var(--sd-success-border);
      border-radius: var(--sd-radius-xl);
      background: var(--sd-surface);
      text-align: center;
      box-shadow: var(--sd-shadow-sm);
    }
    .done-ic {
      display: inline-flex;
      padding: 14px;
      border-radius: 50%;
      background: var(--sd-success-bg);
      color: var(--sd-success);
    }
    .done h1 {
      font-size: clamp(1.4rem, 3.4vw, 1.8rem);
      outline: none;
    }
    .done p {
      max-width: 540px;
      margin: 0;
      color: var(--sd-text-2);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 10px;
      margin-top: 10px;
    }
  `,
})
export class SubmitPage {
  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  private readonly injector = inject(Injector);
  private readonly heading = viewChild<ElementRef<HTMLElement>>('doneHeading');

  protected readonly done = signal<Done | null>(null);

  protected finish(done: Done): void {
    this.done.set(done);
    this.brand.setPageTitle('Ticket submitted');
    afterNextRender(
      () => {
        globalThis.scrollTo?.({ top: 0 });
        this.heading()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }

  protected reset(): void {
    this.done.set(null);
    this.brand.setPageTitle('Submit a ticket');
  }
}

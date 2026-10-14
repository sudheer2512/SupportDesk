import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  IonBackButton,
  IonButton,
  IonButtons,
  IonContent,
  IonHeader,
  IonIcon,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, PortalArticleDto, PortalAttachmentDto, PortalContentApi, formatDate, saveBlob } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { ModeService } from '../core/mobile-session';
import { SessionCoordinator } from '../core/session-coordinator';
import { MobileToasts } from '../core/ui-services';
import { OfflineBanner } from '../shared/offline-banner.component';
import { StateView } from '../shared/state-view.component';

/** Knowledge-base article reader (server-sanitised HTML), attachments, "was this helpful?" and related articles. */
@Component({
  selector: 'm-article',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonButtons,
    IonBackButton,
    IonButton,
    IonIcon,
    IonContent,
    IonList,
    IonListHeader,
    IonItem,
    IonLabel,
    OfflineBanner,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar>
        <ion-buttons slot="start"><ion-back-button defaultHref="/customer/help" text="" aria-label="Back to help" /></ion-buttons>
        <ion-title>{{ article.value()?.title ?? 'Article' }}</ion-title>
      </ion-toolbar>
      <m-offline-banner [staleSince]="article.staleSince()" />
    </ion-header>
    <ion-content class="ion-padding">
      @if (article.value(); as a) {
        <article>
          <p class="crumbs">{{ a.categoryName || 'Help center' }} · updated {{ date(a.updatedAt) }}</p>
          <h1>{{ a.title }}</h1>
          @if (a.translationFallback) {
            <p class="note">This article is not available in your language yet.</p>
          }
          <div class="body" [innerHTML]="a.bodyHtml"></div>
        </article>

        @if (a.attachments.length) {
          <ion-list lines="full">
            <ion-list-header><ion-label>Attachments</ion-label></ion-list-header>
            @for (f of a.attachments; track f.id) {
              <ion-item [button]="true" detail="false" [disabled]="readOnly()" (click)="download(f)">
                <ion-icon slot="start" name="attach-outline" aria-hidden="true" /><ion-label>{{ f.fileName }}</ion-label>
              </ion-item>
            }
          </ion-list>
        }

        <section class="feedback" aria-live="polite">
          @if (voted() === null) {
            <p>Was this article helpful?</p>
            <ion-button fill="outline" [disabled]="readOnly()" (click)="vote(true)"><ion-icon slot="start" name="thumbs-up-outline" aria-hidden="true" />Yes</ion-button>
            <ion-button fill="outline" color="medium" [disabled]="readOnly()" (click)="vote(false)"><ion-icon slot="start" name="thumbs-down-outline" aria-hidden="true" />No</ion-button>
          } @else {
            <p>Thanks for the feedback!{{ voted() ? '' : ' If you still need help, start a new request.' }}</p>
            @if (!voted()) {
              <ion-button routerLink="/customer/new">New request</ion-button>
            }
          }
        </section>

        @if (a.related.length) {
          <ion-list lines="full">
            <ion-list-header><ion-label>Related articles</ion-label></ion-list-header>
            @for (r of a.related; track r.id) {
              <ion-item [routerLink]="['/customer/help/articles', r.slug]" [button]="true">
                <ion-label class="ion-text-wrap">{{ r.title }}</ion-label>
              </ion-item>
            }
          </ion-list>
        }
      } @else if (article.loading()) {
        <m-state kind="loading" title="Loading article" />
      } @else if (article.error(); as err) {
        <m-state [kind]="readOnly() ? 'offline' : 'error'" [title]="err.status === 404 ? 'Article not found' : ''"
          [message]="readOnly() ? 'This article is not saved on this device yet.' : err.userMessage" actionLabel="Try again" (action)="article.load()" />
      }
    </ion-content>
  `,
  styles: `
    .crumbs {
      margin: 0;
      font-size: 0.8rem;
      color: var(--sd-text-2);
    }
    h1 {
      margin: 4px 0 12px;
      font-size: 1.3rem;
      line-height: 1.3;
    }
    .note {
      padding: 8px 12px;
      border-radius: 8px;
      background: var(--sd-info-bg);
      font-size: 0.85rem;
    }
    .body {
      line-height: 1.6;
      overflow-wrap: anywhere;
    }
    .body :is(img, video, iframe, table) {
      max-width: 100%;
    }
    .body table {
      display: block;
      overflow-x: auto;
    }
    .body pre {
      overflow-x: auto;
      padding: 8px;
      background: var(--sd-surface-2);
      border-radius: 6px;
    }
    .feedback {
      margin: 24px 0;
      padding: 12px;
      border-radius: 12px;
      background: var(--sd-surface-2);
      text-align: center;
    }
    .feedback p {
      margin: 0 0 8px;
    }
  `,
})
export class ArticlePage implements OnInit {
  readonly slug = input.required<string>();

  private readonly api = inject(PortalContentApi);
  private readonly mode = inject(ModeService);
  private readonly session = inject(SessionCoordinator);
  private readonly toasts = inject(MobileToasts);

  protected readonly readOnly = this.session.readOnly;
  protected readonly voted = signal<boolean | null>(null);
  protected readonly article = cachedResource<PortalArticleDto>({
    key: () => `kb:article:${this.slug()}`,
    kind: 'article',
    fetch: () => this.api.article(this.mode.brand(), this.slug()),
  });

  ngOnInit(): void {
    void this.article.load();
  }

  protected async vote(helpful: boolean): Promise<void> {
    const a = this.article.value();
    if (!a) return;
    try {
      await firstValueFrom(this.api.feedback(this.mode.brand(), a.id, { helpful }));
      this.voted.set(helpful);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected async download(f: PortalAttachmentDto): Promise<void> {
    try {
      saveBlob(await firstValueFrom(this.api.download(f.url)), f.fileName);
    } catch (e) {
      void this.toasts.error(ApiError.from(e).userMessage);
    }
  }

  protected date(value: string): string {
    return formatDate(value);
  }
}

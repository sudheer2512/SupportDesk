import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  IonChip,
  IonContent,
  IonHeader,
  IonItem,
  IonLabel,
  IonList,
  IonListHeader,
  IonRefresher,
  IonRefresherContent,
  IonSearchbar,
  IonTitle,
  IonToolbar,
} from '@ionic/angular/standalone';
import { ApiError, PortalArticleSummaryDto, PortalCategoryDto, PortalContentApi, PortalHomeDto } from '@sd/core';
import { firstValueFrom } from 'rxjs';
import { cachedResource } from '../core/cached-resource';
import { ModeService } from '../core/mobile-session';
import { NetworkService } from '../core/network.service';
import { OfflineCache } from '../core/offline-cache.service';
import { OfflineBanner } from '../shared/offline-banner.component';
import { StateView } from '../shared/state-view.component';

/** Customer help: knowledge-base search, categories and popular articles (cached for offline reading). */
@Component({
  selector: 'm-help',
  imports: [
    RouterLink,
    IonHeader,
    IonToolbar,
    IonTitle,
    IonContent,
    IonSearchbar,
    IonChip,
    IonLabel,
    IonList,
    IonListHeader,
    IonItem,
    IonRefresher,
    IonRefresherContent,
    OfflineBanner,
    StateView,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ion-header>
      <ion-toolbar><ion-title>Help</ion-title></ion-toolbar>
      <ion-toolbar>
        <ion-searchbar placeholder="Search the help center" [debounce]="400" (ionInput)="search($any($event).detail.value ?? '')" aria-label="Search articles" />
      </ion-toolbar>
      <m-offline-banner [staleSince]="home.staleSince()" />
    </ion-header>
    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>

      @if (q()) {
        @if (searching()) {
          <m-state kind="loading" [lines]="4" title="Searching" />
        } @else if (searchError()) {
          <m-state [kind]="online() ? 'error' : 'offline'" [message]="online() ? searchError()! : 'Search needs a connection — saved articles are below when you clear the search.'" />
        } @else {
          <ion-list lines="full">
            <ion-list-header><ion-label>{{ results().length }} result{{ results().length === 1 ? '' : 's' }} for “{{ q() }}”</ion-label></ion-list-header>
            @for (a of results(); track a.id) {
              <ion-item [routerLink]="['/customer/help/articles', a.slug]" [button]="true">
                <ion-label class="ion-text-wrap">
                  <h3>{{ a.title }}</h3>
                  @if (a.snippetHtml) {
                    <p [innerHTML]="a.snippetHtml"></p>
                  } @else if (a.summary) {
                    <p>{{ a.summary }}</p>
                  }
                </ion-label>
              </ion-item>
            } @empty {
              <m-state kind="empty" title="No articles found" message="Try other words, or start a new request and we will help." />
            }
          </ion-list>
        }
      } @else if (home.value(); as h) {
        @if (h.categories.length) {
          <div class="cats" role="group" aria-label="Categories">
            <ion-chip [outline]="category() !== null" (click)="selectCategory(null)">All</ion-chip>
            @for (c of h.categories; track c.id) {
              <ion-chip [outline]="category()?.id !== c.id" (click)="selectCategory(c)">{{ c.name }} ({{ c.articleCount }})</ion-chip>
            }
          </div>
        }
        <ion-list lines="full">
          <ion-list-header><ion-label>{{ category() ? category()!.name : 'Popular articles' }}</ion-label></ion-list-header>
          @for (a of articles(); track a.id) {
            <ion-item [routerLink]="['/customer/help/articles', a.slug]" [button]="true">
              <ion-label class="ion-text-wrap">
                <h3>{{ a.title }}</h3>
                @if (a.summary) {
                  <p>{{ a.summary }}</p>
                }
              </ion-label>
            </ion-item>
          } @empty {
            @if (categoryLoading()) {
              <m-state kind="loading" [lines]="3" />
            } @else {
              <m-state kind="empty" title="No articles here yet" />
            }
          }
        </ion-list>
        @if (!category() && h.recent.length) {
          <ion-list lines="full">
            <ion-list-header><ion-label>Recently updated</ion-label></ion-list-header>
            @for (a of h.recent; track a.id) {
              <ion-item [routerLink]="['/customer/help/articles', a.slug]" [button]="true">
                <ion-label class="ion-text-wrap"><h3>{{ a.title }}</h3></ion-label>
              </ion-item>
            }
          </ion-list>
        }
      } @else if (home.loading()) {
        <m-state kind="loading" title="Loading the help center" />
      } @else if (home.error()) {
        <m-state [kind]="online() ? 'error' : 'offline'" [message]="online() ? home.error()!.userMessage : 'Open Help once while online to read articles offline.'" actionLabel="Try again" (action)="home.load()" />
      }
    </ion-content>
  `,
  styles: `
    .cats {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      padding: 8px 12px 0;
    }
    ion-chip {
      min-height: 36px;
    }
    :host ::ng-deep mark {
      background: var(--sd-warning-bg);
      color: inherit;
    }
  `,
})
export class HelpPage implements OnInit {
  private readonly api = inject(PortalContentApi);
  private readonly mode = inject(ModeService);
  private readonly cache = inject(OfflineCache);
  private readonly network = inject(NetworkService);

  protected readonly online = this.network.online;
  protected readonly home = cachedResource<PortalHomeDto>({ key: () => 'kb:home', kind: 'article', fetch: () => this.api.home(this.mode.brand()) });
  protected readonly q = signal('');
  protected readonly results = signal<PortalArticleSummaryDto[]>([]);
  protected readonly searching = signal(false);
  protected readonly searchError = signal<string | null>(null);
  protected readonly category = signal<PortalCategoryDto | null>(null);
  protected readonly categoryArticles = signal<PortalArticleSummaryDto[]>([]);
  protected readonly categoryLoading = signal(false);
  protected readonly articles = computed(() => (this.category() ? this.categoryArticles() : (this.home.value()?.popular ?? [])));
  private searchGen = 0;

  ngOnInit(): void {
    void this.home.load();
  }

  protected async refresh(event: Event): Promise<void> {
    await this.home.load();
    await (event.target as HTMLIonRefresherElement).complete();
  }

  protected async search(value: string): Promise<void> {
    const q = value.trim();
    this.q.set(q);
    this.searchError.set(null);
    if (!q) return;
    const gen = ++this.searchGen;
    this.searching.set(true);
    try {
      const result = await firstValueFrom(this.api.search(this.mode.brand(), q));
      if (gen === this.searchGen) this.results.set(result.articles);
    } catch (e) {
      if (gen === this.searchGen) this.searchError.set(ApiError.from(e).userMessage);
    } finally {
      if (gen === this.searchGen) this.searching.set(false);
    }
  }

  protected async selectCategory(c: PortalCategoryDto | null): Promise<void> {
    this.category.set(c);
    if (!c) return;
    const key = `kb:category:${c.slug}`;
    const cached = await this.cache.read<PortalArticleSummaryDto[]>(key);
    this.categoryArticles.set(cached?.value ?? []);
    this.categoryLoading.set(true);
    try {
      const list = await firstValueFrom(this.api.articles(this.mode.brand(), { category: c.slug, limit: 50 }));
      if (this.category()?.id === c.id) this.categoryArticles.set(list);
      void this.cache.write(key, 'article', list);
    } catch {
      /* offline: the cached list (if any) stays */
    } finally {
      this.categoryLoading.set(false);
    }
  }
}

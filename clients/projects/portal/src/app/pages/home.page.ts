import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PortalAuthService } from '@sd/core';
import { SdIcon } from '@sd/ui';
import { BrandService } from '../brand.service';

interface Tile {
  title: string;
  text: string;
  icon: string;
  path: string;
}

/** Brand home: welcome text from the portal config and entry points for the enabled features. */
@Component({
  selector: 'portal-home-page',
  imports: [RouterLink, SdIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="hero">
      <div class="portal-container">
        <h1>{{ brand.name() }}</h1>
        <p class="welcome">
          {{ brand.config()?.portalWelcome || 'How can we help you today?' }}
        </p>
        @if (auth.contact(); as c) {
          <p class="signed">Signed in as <strong>{{ c.fullName }}</strong>{{ c.customerName ? ' · ' + c.customerName : '' }}</p>
        }
      </div>
    </section>
    <section class="portal-container tiles" aria-label="What would you like to do?">
      @for (tile of tiles(); track tile.path) {
        <a class="tile" [routerLink]="base() + tile.path">
          <span class="icon" aria-hidden="true"><sd-icon [name]="tile.icon" [size]="20" /></span>
          <span class="tile-title">{{ tile.title }}</span>
          <span class="tile-text">{{ tile.text }}</span>
        </a>
      }
    </section>
  `,
  styles: `
    .hero {
      padding: 56px 0 40px;
      background:
        radial-gradient(800px 300px at 10% 0%, var(--sd-primary-soft), transparent 70%),
        radial-gradient(600px 260px at 90% 10%, var(--sd-accent-soft), transparent 70%);
    }
    h1 {
      font-size: clamp(1.75rem, 4vw, 2.5rem);
      font-weight: var(--sd-weight-bold);
      letter-spacing: -0.025em;
    }
    .welcome {
      max-width: 680px;
      margin-top: 10px;
      color: var(--sd-text-2);
      font-size: var(--sd-text-lg);
      white-space: pre-line;
    }
    .signed {
      margin-top: 14px;
      color: var(--sd-text-3);
    }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(min(100%, 240px), 1fr));
      gap: 16px;
      margin-top: 8px;
    }
    .tile {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 20px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-lg);
      background: var(--sd-surface);
      color: var(--sd-text);
      box-shadow: var(--sd-shadow-xs);
      text-decoration: none;
      transition: box-shadow var(--sd-dur) var(--sd-ease), border-color var(--sd-dur) var(--sd-ease), transform var(--sd-dur) var(--sd-ease);
    }
    .tile:hover {
      border-color: var(--sd-primary-border);
      box-shadow: var(--sd-shadow-md);
      transform: translateY(-1px);
      text-decoration: none;
    }
    .icon {
      display: inline-flex;
      width: fit-content;
      padding: 9px;
      border-radius: var(--sd-radius-md);
      background: var(--sd-primary-soft);
      color: var(--sd-primary);
      margin-bottom: 6px;
    }
    .tile-title {
      font-weight: var(--sd-weight-semibold);
      font-size: var(--sd-text-lg);
    }
    .tile-text {
      color: var(--sd-text-2);
    }
  `,
})
export class HomePage {
  protected readonly brand = inject(BrandService);
  protected readonly auth = inject(PortalAuthService);
  protected readonly base = computed(() => `/${this.brand.slug() ?? ''}`);
  protected readonly tiles = computed<Tile[]>(() => {
    const f = this.brand.features();
    const tiles: Tile[] = [];
    if (f.kb) tiles.push({ title: 'Knowledge Base', text: 'Answers and how-to guides.', icon: 'book-open', path: '/kb' });
    if (f.community) tiles.push({ title: 'Community', text: 'Ask other customers and our experts.', icon: 'messages', path: '/community' });
    tiles.push({ title: 'Submit a Ticket', text: 'Tell us what you need help with.', icon: 'file-plus', path: '/submit' });
    tiles.push({ title: 'My Tickets', text: 'Follow up on your requests.', icon: 'ticket', path: '/tickets' });
    return tiles;
  });
}

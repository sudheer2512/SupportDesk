import { ChangeDetectionStrategy, Component, computed, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ApiError,
  EngineeringAdminApi,
  EngineeringTrackerDto,
  EngineeringTrackerProvider,
  SaveEngineeringTrackerRequest,
  TrackerFieldMapDto,
  TrackerProjectDto,
  saveErrorMessage,
  validationMessages,
} from '@sd/core';
import { SdButton, SdChipsInput, SdDrawer, SdFormField, SdInput, SdToggle } from '@sd/ui';

interface EditorState {
  name: string;
  isActive: boolean;
  baseUrl: string;
  project: string;
  owner: string;
  repository: string;
  authType: string;
  token: string;
  appPrivateKey: string;
  appId: string;
  installationId: string;
  defaultItemType: string;
  typeBug: string;
  typeFeature: string;
  typeTask: string;
  fields: TrackerFieldMapDto;
  resolvedStates: string[];
  releasedStates: string[];
  pollIntervalMinutes: number;
}

const DEFAULT_FIELDS: Record<EngineeringTrackerProvider, TrackerFieldMapDto> = {
  AzureDevOps: {
    reproStepsField: 'Microsoft.VSTS.TCM.ReproSteps',
    descriptionField: 'System.Description',
    severityField: 'Microsoft.VSTS.Common.Severity',
    priorityField: 'Microsoft.VSTS.Common.Priority',
    fixVersionField: 'Microsoft.VSTS.Build.IntegrationBuild',
    customerImpactField: null,
    defaultAreaPath: null,
    defaultIterationPath: null,
    defaultTags: ['SupportDesk'],
    severityLabelPrefix: null,
    priorityLabelPrefix: null,
  },
  GitHub: {
    reproStepsField: null,
    descriptionField: null,
    severityField: null,
    priorityField: null,
    fixVersionField: null,
    customerImpactField: null,
    defaultAreaPath: null,
    defaultIterationPath: null,
    defaultTags: ['support'],
    severityLabelPrefix: 'severity:',
    priorityLabelPrefix: 'priority:',
  },
  Local: {
    reproStepsField: null,
    descriptionField: null,
    severityField: null,
    priorityField: null,
    fixVersionField: null,
    customerImpactField: null,
    defaultAreaPath: null,
    defaultIterationPath: null,
    defaultTags: [],
    severityLabelPrefix: null,
    priorityLabelPrefix: null,
  },
};

/**
 * Create / edit a tracker connection: Azure DevOps Services or Server (collection URL, project, PAT/OAuth), GitHub
 * (API URL, owner/repository, token or GitHub App) or the built-in board; type, field and state mappings, polling.
 * Secrets are write-only (empty keeps the stored value). Emits the saved tracker (with the webhook secret once).
 */
@Component({
  selector: 'desk-tracker-editor',
  imports: [FormsModule, SdDrawer, SdFormField, SdInput, SdButton, SdToggle, SdChipsInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <sd-drawer [(open)]="open" [title]="heading()" width="640px">
      <form id="tracker-form" class="form" (ngSubmit)="save()">
        <sd-form-field label="Name" optional hint="Shown to agents when they escalate." [error]="errors()['name']">
          <input sdInput name="name" maxlength="200" [ngModel]="state().name" (ngModelChange)="patch({ name: $event })" />
        </sd-form-field>

        @if (provider() === 'AzureDevOps') {
          <fieldset>
            <legend>Connection</legend>
            <sd-form-field label="Organization / collection URL" required hint="Azure DevOps Services: https://dev.azure.com/your-org · Server/TFS: https://tfs.example.com/tfs/DefaultCollection" [error]="errors()['baseUrl']">
              <input sdInput name="baseUrl" type="url" [ngModel]="state().baseUrl" (ngModelChange)="patch({ baseUrl: $event })" />
            </sd-form-field>
            <sd-form-field label="Project" required [error]="errors()['project']">
              <input sdInput name="project" list="tracker-projects" [ngModel]="state().project" (ngModelChange)="patch({ project: $event })" />
            </sd-form-field>
            <div class="two">
              <sd-form-field label="Authentication" [error]="errors()['authType']">
                <select sdInput name="authType" [ngModel]="state().authType" (ngModelChange)="patch({ authType: $event })">
                  <option value="Pat">Personal access token</option>
                  <option value="OAuth">OAuth access token</option>
                </select>
              </sd-form-field>
              <sd-form-field [label]="state().authType === 'OAuth' ? 'Access token' : 'Personal access token'" [hint]="tracker()?.hasToken ? 'Stored — leave empty to keep it.' : 'Scopes: Work Items (Read & write), Project and Team (Read).'" [error]="errors()['token']">
                <input sdInput name="token" type="password" autocomplete="new-password" [ngModel]="state().token" (ngModelChange)="patch({ token: $event })" />
              </sd-form-field>
            </div>
          </fieldset>
        } @else if (provider() === 'GitHub') {
          <fieldset>
            <legend>Connection</legend>
            <sd-form-field label="API URL" hint="https://api.github.com, or https://github.example.com/api/v3 for GitHub Enterprise Server." [error]="errors()['baseUrl']">
              <input sdInput name="baseUrl" type="url" [ngModel]="state().baseUrl" (ngModelChange)="patch({ baseUrl: $event })" />
            </sd-form-field>
            <div class="two">
              <sd-form-field label="Owner" required [error]="errors()['owner']">
                <input sdInput name="owner" [ngModel]="state().owner" (ngModelChange)="patch({ owner: $event })" />
              </sd-form-field>
              <sd-form-field label="Repository" required [error]="errors()['repository']">
                <input sdInput name="repository" list="tracker-projects" [ngModel]="state().repository" (ngModelChange)="patch({ repository: $event })" />
              </sd-form-field>
            </div>
            <sd-form-field label="Authentication" [error]="errors()['authType']">
              <select sdInput name="authType" [ngModel]="state().authType" (ngModelChange)="patch({ authType: $event })">
                <option value="Token">Personal / fine-grained token</option>
                <option value="App">GitHub App installation</option>
              </select>
            </sd-form-field>
            @if (state().authType === 'App') {
              <div class="two">
                <sd-form-field label="App id" required [error]="errors()['appId']">
                  <input sdInput name="appId" inputmode="numeric" [ngModel]="state().appId" (ngModelChange)="patch({ appId: $event })" />
                </sd-form-field>
                <sd-form-field label="Installation id" required [error]="errors()['installationId']">
                  <input sdInput name="installationId" inputmode="numeric" [ngModel]="state().installationId" (ngModelChange)="patch({ installationId: $event })" />
                </sd-form-field>
              </div>
              <sd-form-field label="Private key (PEM)" [hint]="tracker()?.hasAppPrivateKey ? 'Stored — leave empty to keep it.' : 'App permissions: Issues (Read & write), Metadata (Read), Contents (Read) for releases.'" [error]="errors()['appPrivateKey']">
                <textarea sdInput name="appPrivateKey" rows="4" autocomplete="off" spellcheck="false" [ngModel]="state().appPrivateKey" (ngModelChange)="patch({ appPrivateKey: $event })"></textarea>
              </sd-form-field>
            } @else {
              <sd-form-field label="Token" [hint]="tracker()?.hasToken ? 'Stored — leave empty to keep it.' : 'Fine-grained token with Issues: Read & write and Metadata/Contents: Read on the repository.'" [error]="errors()['token']">
                <input sdInput name="token" type="password" autocomplete="new-password" [ngModel]="state().token" (ngModelChange)="patch({ token: $event })" />
              </sd-form-field>
            }
          </fieldset>
        } @else {
          <p class="sd-muted info">The built-in tracker keeps work items and releases inside SupportDesk. Engineers (light agents with engineering.manage) work them on the Engineering board.</p>
        }
        @if (projects().length > 0) {
          <datalist id="tracker-projects">
            @for (p of projects(); track p.id) {
              <option [value]="provider() === 'GitHub' ? p.name.split('/').pop() : p.name"></option>
            }
          </datalist>
        }
        @if (tracker() && provider() !== 'Local') {
          <div class="row">
            <button sdButton type="button" size="sm" icon="list" [loading]="loadingProjects()" (click)="loadProjects()">{{ provider() === 'GitHub' ? 'Load repositories' : 'Load projects' }}</button>
            @if (projectsError()) {
              <span class="error small" role="alert">{{ projectsError() }}</span>
            } @else if (projects().length > 0) {
              <span class="sd-muted small">{{ projects().length }} found — pick one in the field above.</span>
            }
          </div>
        }

        @if (provider() !== 'Local') {
          <fieldset>
            <legend>Type mapping</legend>
            <div class="three">
              <sd-form-field label="Bug →">
                <input sdInput name="typeBug" [ngModel]="state().typeBug" (ngModelChange)="patch({ typeBug: $event })" />
              </sd-form-field>
              <sd-form-field label="Feature →">
                <input sdInput name="typeFeature" [ngModel]="state().typeFeature" (ngModelChange)="patch({ typeFeature: $event })" />
              </sd-form-field>
              <sd-form-field label="Task →">
                <input sdInput name="typeTask" [ngModel]="state().typeTask" (ngModelChange)="patch({ typeTask: $event })" />
              </sd-form-field>
            </div>
            <p class="sd-muted small">{{ provider() === 'GitHub' ? 'Labels added to new issues.' : 'Azure DevOps work item types (e.g. Bug, User Story, Product Backlog Item, Task).' }}</p>
          </fieldset>

          <fieldset>
            <legend>Field mapping</legend>
            @if (provider() === 'AzureDevOps') {
              <div class="two">
                <sd-form-field label="Repro steps field">
                  <input sdInput name="repro" [ngModel]="state().fields.reproStepsField" (ngModelChange)="patchField({ reproStepsField: $event })" />
                </sd-form-field>
                <sd-form-field label="Description field (non-bugs)">
                  <input sdInput name="desc" [ngModel]="state().fields.descriptionField" (ngModelChange)="patchField({ descriptionField: $event })" />
                </sd-form-field>
                <sd-form-field label="Severity field">
                  <input sdInput name="sev" [ngModel]="state().fields.severityField" (ngModelChange)="patchField({ severityField: $event })" />
                </sd-form-field>
                <sd-form-field label="Priority field">
                  <input sdInput name="pri" [ngModel]="state().fields.priorityField" (ngModelChange)="patchField({ priorityField: $event })" />
                </sd-form-field>
                <sd-form-field label="Fix version field" hint="Read back as the fix version.">
                  <input sdInput name="fix" [ngModel]="state().fields.fixVersionField" (ngModelChange)="patchField({ fixVersionField: $event })" />
                </sd-form-field>
                <sd-form-field label="Customer impact field" optional hint="Integer field, e.g. Custom.CustomerImpact.">
                  <input sdInput name="impact" [ngModel]="state().fields.customerImpactField" (ngModelChange)="patchField({ customerImpactField: $event })" />
                </sd-form-field>
                <sd-form-field label="Default area path" optional>
                  <input sdInput name="area" [ngModel]="state().fields.defaultAreaPath" (ngModelChange)="patchField({ defaultAreaPath: $event })" />
                </sd-form-field>
                <sd-form-field label="Default iteration path" optional>
                  <input sdInput name="iteration" [ngModel]="state().fields.defaultIterationPath" (ngModelChange)="patchField({ defaultIterationPath: $event })" />
                </sd-form-field>
              </div>
            } @else {
              <div class="two">
                <sd-form-field label="Severity label prefix">
                  <input sdInput name="sevPrefix" [ngModel]="state().fields.severityLabelPrefix" (ngModelChange)="patchField({ severityLabelPrefix: $event })" />
                </sd-form-field>
                <sd-form-field label="Priority label prefix">
                  <input sdInput name="priPrefix" [ngModel]="state().fields.priorityLabelPrefix" (ngModelChange)="patchField({ priorityLabelPrefix: $event })" />
                </sd-form-field>
              </div>
              <p class="sd-muted small">The issue milestone is the fix version; a published release whose tag or name matches it marks the fix as released.</p>
            }
            <sd-form-field [label]="provider() === 'GitHub' ? 'Default labels' : 'Default tags'" optional>
              <sd-chips-input ariaLabel="Default tags" [value]="state().fields.defaultTags" (valueChange)="patchField({ defaultTags: $event })" />
            </sd-form-field>
          </fieldset>

          <fieldset>
            <legend>State mapping</legend>
            <sd-form-field label="Resolved when the state is" hint="Tracker states, or label:x / tag:x." [error]="errors()['resolvedStates']">
              <sd-chips-input ariaLabel="Resolved states" [value]="state().resolvedStates" (valueChange)="patch({ resolvedStates: $event })" />
            </sd-form-field>
            <sd-form-field label="Released when the state is" hint="Also released when a release/deployment of the fix version is reported." [error]="errors()['releasedStates']">
              <sd-chips-input ariaLabel="Released states" [value]="state().releasedStates" (valueChange)="patch({ releasedStates: $event })" />
            </sd-form-field>
          </fieldset>

          <div class="two">
            <sd-form-field label="Default item type">
              <select sdInput name="defaultType" [ngModel]="state().defaultItemType" (ngModelChange)="patch({ defaultItemType: $event })">
                <option value="Bug">Bug</option>
                <option value="Feature">Feature</option>
                <option value="Task">Task</option>
              </select>
            </sd-form-field>
            <sd-form-field label="Poll every (minutes)" hint="Fallback when webhooks are not configured." [error]="errors()['pollIntervalMinutes']">
              <input sdInput name="poll" type="number" min="5" max="1440" [ngModel]="state().pollIntervalMinutes" (ngModelChange)="patch({ pollIntervalMinutes: +$event })" />
            </sd-form-field>
          </div>
        }
        <sd-toggle [value]="state().isActive" (valueChange)="patch({ isActive: $event })">Active</sd-toggle>
        @if (formError()) {
          <p class="error" role="alert">{{ formError() }}</p>
        }
      </form>
      <div sdDrawerFooter class="footer">
        <button sdButton type="button" variant="ghost" (click)="open.set(false)">Cancel</button>
        <button sdButton type="submit" form="tracker-form" variant="primary" [loading]="saving()">{{ tracker() ? 'Save' : 'Create tracker' }}</button>
      </div>
    </sd-drawer>
  `,
  styles: `
    .form {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    fieldset {
      display: flex;
      flex-direction: column;
      gap: 10px;
      margin: 0;
      padding: 12px;
      border: 1px solid var(--sd-border);
      border-radius: var(--sd-radius-md);
    }
    legend {
      padding: 0 4px;
      font-size: var(--sd-text-sm);
      font-weight: var(--sd-weight-semibold);
    }
    .two {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 10px;
    }
    .three {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 10px;
    }
    @media (max-width: 560px) {
      .two,
      .three {
        grid-template-columns: 1fr;
      }
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }
    .small {
      margin: 0;
      font-size: var(--sd-text-xs);
    }
    .info {
      margin: 0;
    }
    .error {
      margin: 0;
      color: var(--sd-danger);
      font-size: var(--sd-text-sm);
    }
    .footer {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
    }
  `,
})
export class TrackerEditorComponent {
  readonly open = model(false);
  /** Null → create a tracker of {@link newProvider}. */
  readonly tracker = input<EngineeringTrackerDto | null>(null);
  readonly newProvider = input<EngineeringTrackerProvider>('AzureDevOps');
  readonly saved = output<EngineeringTrackerDto>();

  private readonly api = inject(EngineeringAdminApi);

  protected readonly provider = computed<EngineeringTrackerProvider>(() => this.tracker()?.provider ?? this.newProvider());
  protected readonly heading = computed(() => {
    const p = this.provider();
    const label = p === 'AzureDevOps' ? 'Azure DevOps' : p === 'GitHub' ? 'GitHub' : 'Built-in board';
    return this.tracker() ? `Edit ${label} tracker` : `Connect ${label}`;
  });
  protected readonly state = signal<EditorState>(this.blank('AzureDevOps'));
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly projects = signal<TrackerProjectDto[]>([]);
  protected readonly loadingProjects = signal(false);
  protected readonly projectsError = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (!this.open()) return;
      const t = this.tracker();
      const p = this.provider();
      untracked(() => {
        this.errors.set({});
        this.formError.set(null);
        this.projects.set([]);
        this.projectsError.set(null);
        this.state.set(t ? this.fromTracker(t) : this.blank(p));
      });
    });
  }

  private blank(p: EngineeringTrackerProvider): EditorState {
    return {
      name: '',
      isActive: true,
      baseUrl: p === 'GitHub' ? 'https://api.github.com' : '',
      project: '',
      owner: '',
      repository: '',
      authType: p === 'GitHub' ? 'Token' : 'Pat',
      token: '',
      appPrivateKey: '',
      appId: '',
      installationId: '',
      defaultItemType: 'Bug',
      typeBug: p === 'GitHub' ? 'bug' : 'Bug',
      typeFeature: p === 'GitHub' ? 'enhancement' : 'User Story',
      typeTask: p === 'GitHub' ? 'task' : 'Task',
      fields: { ...DEFAULT_FIELDS[p], defaultTags: [...DEFAULT_FIELDS[p].defaultTags] },
      resolvedStates: p === 'GitHub' ? ['closed'] : p === 'Local' ? ['Resolved', 'Closed'] : ['Resolved', 'Done'],
      releasedStates: p === 'GitHub' ? ['label:released'] : p === 'Local' ? [] : ['Closed'],
      pollIntervalMinutes: 15,
    };
  }

  private fromTracker(t: EngineeringTrackerDto): EditorState {
    const map = (k: string, fallback: string) => t.typeMap[k] ?? t.typeMap[k.toLowerCase()] ?? fallback;
    return {
      name: t.name,
      isActive: t.isActive,
      baseUrl: t.baseUrl ?? '',
      project: t.project ?? '',
      owner: t.owner ?? '',
      repository: t.repository ?? '',
      authType: t.authType,
      token: '',
      appPrivateKey: '',
      appId: t.appId ?? '',
      installationId: t.installationId ?? '',
      defaultItemType: t.defaultItemType,
      typeBug: map('Bug', 'Bug'),
      typeFeature: map('Feature', 'Feature'),
      typeTask: map('Task', 'Task'),
      fields: { ...t.fields, defaultTags: [...t.fields.defaultTags] },
      resolvedStates: [...t.resolvedStates],
      releasedStates: [...t.releasedStates],
      pollIntervalMinutes: t.pollIntervalMinutes || 15,
    };
  }

  protected patch(change: Partial<EditorState>): void {
    this.state.update((s) => ({ ...s, ...change }));
  }

  protected patchField(change: Partial<TrackerFieldMapDto>): void {
    this.state.update((s) => ({ ...s, fields: { ...s.fields, ...change } }));
  }

  protected loadProjects(): void {
    const t = this.tracker();
    if (!t) return;
    this.loadingProjects.set(true);
    this.projectsError.set(null);
    this.api.projects(t.id).subscribe({
      next: (p) => {
        this.projects.set(p);
        this.loadingProjects.set(false);
        if (p.length === 0) this.projectsError.set('Nothing is visible to these credentials.');
      },
      error: (err: unknown) => {
        this.loadingProjects.set(false);
        this.projectsError.set(err instanceof ApiError ? err.userMessage : 'Could not load projects.');
      },
    });
  }

  protected save(): void {
    const s = this.state();
    const p = this.provider();
    const t = this.tracker();
    const trim = (v: string) => v.trim() || null;
    const request: SaveEngineeringTrackerRequest = {
      provider: p,
      name: trim(s.name),
      isActive: s.isActive,
      baseUrl: p === 'Local' ? null : trim(s.baseUrl),
      project: p === 'AzureDevOps' ? trim(s.project) : null,
      owner: p === 'GitHub' ? trim(s.owner) : null,
      repository: p === 'GitHub' ? trim(s.repository) : null,
      authType: p === 'Local' ? null : s.authType,
      token: s.token ? s.token.trim() : null,
      appPrivateKey: s.appPrivateKey ? s.appPrivateKey.trim() : null,
      appId: p === 'GitHub' && s.authType === 'App' ? trim(s.appId) : null,
      installationId: p === 'GitHub' && s.authType === 'App' ? trim(s.installationId) : null,
      defaultItemType: s.defaultItemType,
      typeMap: { Bug: s.typeBug.trim() || 'Bug', Feature: s.typeFeature.trim() || 'Feature', Task: s.typeTask.trim() || 'Task' },
      fields: s.fields,
      resolvedStates: s.resolvedStates,
      releasedStates: s.releasedStates,
      pollIntervalMinutes: p === 'Local' ? 15 : s.pollIntervalMinutes,
      rowVersion: t?.rowVersion ?? null,
    };
    this.saving.set(true);
    this.errors.set({});
    this.formError.set(null);
    (t ? this.api.update(t.id, request) : this.api.create(request)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.open.set(false);
        this.saved.emit(saved);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const fields = validationMessages(err);
        this.errors.set(fields);
        this.formError.set(Object.keys(fields).length > 0 ? null : saveErrorMessage(err));
      },
    });
  }
}

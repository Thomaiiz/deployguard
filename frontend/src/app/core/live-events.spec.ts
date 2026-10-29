import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { EVENT_SOURCE_FACTORY, LIVE_RETRY_MS, LiveEventsService } from './live-events';
import { LiveMessage } from './models';

/** EventSource falso: o teste decide quando o "servidor" manda cada mensagem. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  readyState = 0; // CONNECTING
  closed = false;
  onerror: (() => void) | null = null;
  private readonly listeners = new Map<string, ((e: MessageEvent) => void)[]>();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(name: string, listener: (e: MessageEvent) => void): void {
    this.listeners.set(name, [...(this.listeners.get(name) ?? []), listener]);
  }

  emit(name: string, data = ''): void {
    this.readyState = 1; // OPEN
    this.listeners.get(name)?.forEach((l) => l(new MessageEvent(name, { data })));
  }

  fail(readyState: number): void {
    this.readyState = readyState;
    this.onerror?.();
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }
}

describe('LiveEventsService', () => {
  let service: LiveEventsService;
  let subscription: Subscription | undefined;

  beforeEach(() => {
    FakeEventSource.instances = [];
    TestBed.configureTestingModule({
      providers: [
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    service = TestBed.inject(LiveEventsService);
  });

  afterEach(() => {
    subscription?.unsubscribe();
    vi.useRealTimers();
  });

  it('connects to /api/events and parses events', () => {
    const received: LiveMessage[] = [];
    subscription = service.messages$.subscribe((m) => received.push(m));

    const source = FakeEventSource.instances[0];
    expect(source.url).toBe('/api/events');
    expect(service.status()).toBe('connecting');

    source.emit('ready');
    expect(service.status()).toBe('live');

    source.emit(
      'check',
      JSON.stringify({ type: 'CheckCompleted', applicationId: 'abc', status: 'Down' }),
    );
    expect(received.map((m) => m.name)).toEqual(['ready', 'check']);
    expect(received[1].event?.applicationId).toBe('abc');
  });

  it('shares one connection between subscribers', () => {
    subscription = service.messages$.subscribe();
    subscription.add(service.messages$.subscribe());
    expect(FakeEventSource.instances.length).toBe(1);
  });

  it('retries after a refusal (401 or 503) closes the connection', () => {
    vi.useFakeTimers();
    subscription = service.messages$.subscribe();
    const first = FakeEventSource.instances[0];

    first.fail(1); // erro passageiro: o navegador reconecta sozinho
    expect(service.status()).toBe('connecting');
    expect(FakeEventSource.instances.length).toBe(1);

    first.fail(2); // CLOSED: o navegador desistiu
    expect(service.status()).toBe('offline');
    expect(first.closed).toBe(true);

    vi.advanceTimersByTime(LIVE_RETRY_MS);
    expect(FakeEventSource.instances.length).toBe(2);
  });

  it('closes the connection when nobody listens anymore', () => {
    vi.useFakeTimers();
    service.messages$.subscribe().unsubscribe();
    const source = FakeEventSource.instances[0];
    expect(source.closed).toBe(false); // espera um pouco: pode ser só troca de tela

    vi.advanceTimersByTime(5_000);
    expect(source.closed).toBe(true);
    expect(service.status()).toBe('offline');
  });
});

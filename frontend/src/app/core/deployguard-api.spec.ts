import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { DeployGuardApi } from './deployguard-api';

describe('DeployGuardApi', () => {
  let api: DeployGuardApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(DeployGuardApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify()); // garante que não sobrou requisição inesperada

  it('lists applications from the relative /api path', () => {
    let result: unknown;
    api.listApplications().subscribe((apps) => (result = apps));

    const request = http.expectOne('/api/applications');
    expect(request.request.method).toBe('GET');
    request.flush([]);

    expect(result).toEqual([]);
  });

  it('sends limit and cursor when paging history', () => {
    api.listChecks('abc', { limit: 10, cursor: 'xyz' }).subscribe();

    const request = http.expectOne((r) => r.url === '/api/applications/abc/checks');
    expect(request.request.params.get('limit')).toBe('10');
    expect(request.request.params.get('cursor')).toBe('xyz');
    request.flush({ items: [], nextCursor: null });
  });

  it('encodes ids in the URL', () => {
    api.getApplication('../admin').subscribe();

    http.expectOne('/api/applications/..%2Fadmin').flush({});
  });

  it('posts new applications', () => {
    api
      .createApplication({
        name: 'A',
        healthCheckUrl: 'https://a.example.com',
        checkIntervalSeconds: 60,
      })
      .subscribe();

    const request = http.expectOne('/api/applications');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.name).toBe('A');
    request.flush({});
  });

  it('filters incidents by status', () => {
    api.listIncidents('Open').subscribe();

    const request = http.expectOne((r) => r.url === '/api/incidents');
    expect(request.request.params.get('status')).toBe('Open');
    request.flush([]);
  });
});

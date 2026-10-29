import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { AuthService, safeReturnUrl } from './auth';
import { adminGuard, authGuard } from './auth.guard';
import { authInterceptor } from './auth.interceptor';

const admin = { id: '1', email: 'admin@example.com', role: 'Admin' as const };
const viewer = { id: '2', email: 'viewer@example.com', role: 'Viewer' as const };

describe('Auth', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Executa um guard dentro do contexto de injeção do Angular. */
  function runGuard(guard: typeof authGuard, url = '/applications/1') {
    return TestBed.runInInjectionContext(() => guard({} as never, { url } as never)) as Promise<
      boolean | UrlTree
    >;
  }

  it('loads the session once from /api/auth/me', async () => {
    const first = auth.ensureSession();
    const second = auth.ensureSession();
    http.expectOne('/api/auth/me').flush(admin); // uma requisição só para as duas chamadas

    expect(await first).toEqual(admin);
    expect(await second).toEqual(admin);
    expect(auth.isAdmin()).toBe(true);
  });

  it('treats 401 on /me as logged out', async () => {
    const session = auth.ensureSession();
    http.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await session).toBeNull();
    expect(auth.user()).toBeNull();
  });

  it('auth guard redirects to login keeping the requested page', async () => {
    const result = runGuard(authGuard);
    http.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    const tree = (await result) as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(tree)).toBe('/login?returnUrl=%2Fapplications%2F1');
  });

  it('admin guard sends viewers back to the dashboard', async () => {
    const result = runGuard(adminGuard);
    http.expectOne('/api/auth/me').flush(viewer);

    expect(TestBed.inject(Router).serializeUrl((await result) as UrlTree)).toBe('/');
  });

  it('login stores the user and logout clears it', () => {
    auth.login('admin@example.com', 'segredo').subscribe();
    const login = http.expectOne('/api/auth/login');
    expect(login.request.body).toEqual({ email: 'admin@example.com', password: 'segredo' });
    login.flush(admin);
    expect(auth.user()).toEqual(admin);

    auth.logout().subscribe();
    http.expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
    expect(auth.user()).toBeNull();
  });

  it('interceptor ends the session when the API answers 401', () => {
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    auth.user.set(admin);

    TestBed.inject(HttpClient)
      .get('/api/applications')
      .subscribe({ error: () => undefined });
    http.expectOne('/api/applications').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.user()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.anything());
  });

  it('interceptor ignores 401 from the login endpoint (wrong password)', () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate');

    auth.login('admin@example.com', 'errada').subscribe({ error: () => undefined });
    http.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([
    ['/applications/1', '/applications/1'],
    ['https://evil.example.com', '/'],
    ['//evil.example.com', '/'],
    ['/\\evil.example.com', '/'],
    [undefined, '/'],
  ])('safeReturnUrl(%s) -> %s (bloqueia open redirect)', (input, expected) => {
    expect(safeReturnUrl(input)).toBe(expected);
  });
});

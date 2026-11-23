import { render } from 'preact';
import { registerSW } from 'virtual:pwa-register';
import { App } from './App';
import './styles.css';

render(<App />, document.getElementById('app')!);

// Offline support: the service worker caches the app after the first visit. Updates apply on next load.
if ('serviceWorker' in navigator && import.meta.env.PROD) registerSW({ immediate: true });

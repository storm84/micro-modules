const $ = (id) => document.getElementById(id);
const pageSize = 20;
let offset = 0;
let selectedId = null;
let selectionVersion = 0;
let listVersion = 0;
let lastAttempt = null;

function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}
function notice(id, text = '', error = false) {
  $(id).textContent = text;
  $(id).classList.toggle('error', error);
}
function statusBadge(status) {
  const badge = node('span', status === 'InTransit' ? 'In transit' : status, 'status');
  badge.dataset.status = status;
  return badge;
}
function timeElement(value) {
  const time = node('time', new Date(value).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }));
  time.dateTime = value;
  return time;
}
async function api(path, options) {
  const response = await fetch(path, options);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.message || 'The request could not be completed. Try again.');
  return body;
}
function markSelection() {
  document.querySelectorAll('.shipment').forEach((button) => {
    const selected = button.dataset.id === selectedId;
    button.classList.toggle('selected', selected);
    button.setAttribute('aria-pressed', String(selected));
  });
}
async function loadShipments() {
  const version = ++listVersion;
  $('refresh').disabled = true;
  $('shipment-list').setAttribute('aria-busy', 'true');
  notice('page-message');
  try {
    const page = await api(`/api/shipments?offset=${offset}&limit=${pageSize}`);
    if (version !== listVersion) return;
    const items = page.shipments.map((shipment) => {
      const button = node('button', undefined, 'shipment');
      button.type = 'button';
      button.dataset.id = shipment.id;
      const top = node('div', undefined, 'shipment-top');
      top.append(node('span', shipment.recipientName, 'shipment-name'), statusBadge(shipment.status));
      const bottom = node('div', undefined, 'shipment-bottom');
      bottom.append(node('span', shipment.trackingNumber, 'shipment-code'));
      button.append(top, node('div', `${shipment.destinationCity} · ${shipment.service}`, 'shipment-destination'), bottom);
      button.addEventListener('click', () => selectShipment(shipment.id));
      return button;
    });
    $('shipment-list').replaceChildren(...(items.length ? items : [node('p', 'No shipments here yet. Run a parcel demo to create your first shipment.', 'placeholder')]));
    $('page-range').textContent = items.length ? `${offset + 1}–${offset + items.length}` : '0 shipments';
    $('previous').disabled = offset === 0;
    $('next').disabled = !page.hasMore;
    markSelection();
    if (!selectedId && page.shipments.length) await selectShipment(page.shipments[0].id);
    else if (selectedId) await refreshDetails(selectedId, selectionVersion);
  } catch (error) {
    if (version === listVersion) {
      notice('page-message', error.message, true);
      if (!$('shipment-list').querySelector('.shipment'))
        $('shipment-list').replaceChildren(node('p', 'Shipments could not be loaded. Try Refresh shipments.', 'placeholder'));
    }
  } finally {
    if (version === listVersion) {
      $('refresh').disabled = false;
      $('shipment-list').setAttribute('aria-busy', 'false');
    }
  }
}
async function selectShipment(id) {
  selectedId = id;
  const version = ++selectionVersion;
  lastAttempt = null;
  $('notification-form').reset();
  notice('send-message');
  $('detail-content').hidden = true;
  $('detail-empty').hidden = false;
  $('detail-heading').textContent = 'Loading shipment…';
  $('detail-empty-message').textContent = 'Fetching tracking history and customer messages.';
  notice('page-message');
  markSelection();
  try { await refreshDetails(id, version); }
  catch (error) {
    if (version === selectionVersion) {
      $('detail-heading').textContent = 'Shipment unavailable';
      $('detail-empty-message').textContent = 'Refresh shipments or select another parcel to try again.';
      notice('page-message', error.message, true);
    }
  }
}
async function refreshDetails(id, version) {
  const details = await api(`/api/shipments/${id}`);
  if (id !== selectedId || version !== selectionVersion) return null;
  const shipment = details.shipment;
  $('detail-empty').hidden = true;
  $('detail-content').hidden = false;
  $('recipient-name').textContent = shipment.recipient.name;
  $('tracking-number').textContent = shipment.trackingNumber;
  $('shipment-status').replaceWith(Object.assign(statusBadge(shipment.status), { id: 'shipment-status' }));
  const address = (value) => `${value.street}, ${value.postalCode} ${value.city}`;
  const facts = [
    ['From', `${shipment.sender.name} · ${address(shipment.sender)}`],
    ['To', address(shipment.recipient)],
    ['Service', `${shipment.quote.carrier} · ${shipment.quote.service}`],
    ['Parcel', `${shipment.quote.weightGrams / 1000} kg · ${shipment.quote.price.toFixed(2)} ${shipment.quote.currency}`],
  ];
  $('shipment-facts').replaceChildren(...facts.map(([label, value]) => {
    const item = node('div'); item.append(node('dt', label), node('dd', value)); return item;
  }));
  $('tracking-history').replaceChildren(...shipment.events.map((event) => {
    const item = node('li'); item.append(node('span', event.status === 'InTransit' ? 'In transit' : event.status), timeElement(event.occurredAt)); return item;
  }));
  $('notification-count').textContent = String(details.notifications.length);
  const messages = details.notifications.map((message) => {
    const item = node('article', undefined, 'message');
    item.dataset.id = message.id;
    const meta = node('div', undefined, 'message-meta');
    meta.append(node('span', message.kind === 'custom' ? 'Custom message' : 'Automatic message'), timeElement(message.createdAt));
    item.append(meta, node('h4', message.subject), node('p', message.body, 'message-body'));
    return item;
  });
  $('notification-list').replaceChildren(...(messages.length ? messages : [node('p', 'No messages delivered yet. Queued messages will appear here after delivery.', 'muted')]));
  $('notification-email').textContent = shipment.customerEmail;
  $('served-by').textContent = `Shipment served by ${details.servedBy}`;
  return details;
}
$('refresh').addEventListener('click', loadShipments);
$('previous').addEventListener('click', () => { offset = Math.max(0, offset - pageSize); loadShipments(); });
$('next').addEventListener('click', () => { offset += pageSize; loadShipments(); });
$('notification-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  if (!selectedId) return;
  const id = selectedId;
  const version = selectionVersion;
  const subject = $('subject').value;
  const body = $('body').value;
  const fingerprint = JSON.stringify({ id, subject, body });
  // Retain the operation ID after a failed response so Retry cannot queue the same draft twice.
  if (lastAttempt?.fingerprint !== fingerprint) lastAttempt = { fingerprint, operationId: crypto.randomUUID() };
  const operationId = lastAttempt.operationId;
  $('send').disabled = true;
  $('subject').disabled = true;
  $('body').disabled = true;
  notice('send-message', 'Sending notification…');
  let queued = false;
  try {
    await api(`/api/shipments/${id}/notifications`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ operationId, subject, body }),
    });
    queued = true;
    if (version !== selectionVersion) return;
    lastAttempt = null;
    $('notification-form').reset();
    notice('send-message', 'Notification queued. Waiting for delivery to the demo inbox…');
    for (let attempt = 0; attempt < 15; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 1000));
      if (version !== selectionVersion) return;
      const details = await refreshDetails(id, version);
      if (details?.notifications.some((message) => message.id === operationId)) {
        notice('send-message', 'Notification delivered to the demo inbox.');
        return;
      }
    }
    notice('send-message', 'Notification is queued. Refresh shipments to check delivery.');
  } catch (error) {
    if (version === selectionVersion) notice('send-message', queued
      ? 'Notification is queued. Delivery status could not be refreshed; try Refresh shipments.'
      : error.message, !queued);
  } finally {
    $('send').disabled = false;
    $('subject').disabled = false;
    $('body').disabled = false;
  }
});
loadShipments();

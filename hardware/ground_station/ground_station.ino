#include <WiFi.h>
#include <WebServer.h>
#include <SPI.h>
#include <LoRa.h>

WebServer server(80);

// Optimized ESP32 LoRa Pins
const int csPin = 5;      // G5 (CS / NSS)
const int resetPin = 22;  // G22 (RST)
const int irqPin = -1;    // Disabled (polling)

// Anchor (Base Station) GPS Data
String anchorLat = "0.0";
String anchorLng = "0.0";

// Timers
unsigned long lastGpsTime = 0;
const unsigned long gpsTimeout = 4000; // 4 seconds timeout for phone disconnect
unsigned long lastSerialTime = 0;

// Webpage HTML with Robust Continuous GPS Polling
const char PAGE_HTML[] PROGMEM = R"=====(
<!DOCTYPE html>
<html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<style>body{font-family: Arial; text-align: center; margin-top: 50px; background-color: #f4f4f9;}</style>
<title>Ground Station</title></head><body>
<h2>Ground Station Anchor</h2>
<p style="color: #666;">Connected to: <b>GroundStation</b></p>
<p id="status">Waiting for location...</p>
<h3 id="coords" style="color: blue;"></h3>
<script>
  // Switch to watchPosition (event-driven) — fires only when GPS actually changes.
  // This avoids hammering the ESP32 HTTP server 2x per second with cached coordinates.
  let currentLat = "0.0";
  let currentLng = "0.0";
  function sendUpdate(lat, lng) {
    fetch('/update?lat=' + lat + '&lng=' + lng).catch(e => console.error(e));
  }
  if (navigator.geolocation) {
    navigator.geolocation.watchPosition(
      function(pos) {
        currentLat = pos.coords.latitude.toFixed(6);
        currentLng = pos.coords.longitude.toFixed(6);
        document.getElementById("status").innerHTML = "Anchor fixed. Streaming to Unity...";
        document.getElementById("coords").innerHTML = "Anchor Lat: " + currentLat + "<br>Anchor Lng: " + currentLng;
        sendUpdate(currentLat, currentLng);
      },
      function(err) {
        document.getElementById("status").innerHTML = "<span style='color:orange'>GPS: " + err.message + "</span>";
      },
      { enableHighAccuracy: true, timeout: 10000, maximumAge: 2000 }
    );
  }
</script>
</body></html>
)=====";

void handleRoot() { 
  server.send(200, "text/html", PAGE_HTML); 
}

void handleUpdate() {
  if (server.hasArg("lat") && server.hasArg("lng")) {
    anchorLat = server.arg("lat");
    anchorLng = server.arg("lng");
    
    lastGpsTime = millis(); // Reset watchdog timer
    
    server.send(200, "text/plain", "OK");
  } else {
    server.send(400, "text/plain", "Missing args");
  }
}

void setup() {
  Serial.begin(115200);
  while (!Serial);

  LoRa.setPins(csPin, resetPin, irqPin);

  if (!LoRa.begin(433E6)) {
    Serial.println("\n[ERROR] ESP32 LoRa init failed!");
    while (true) delay(1000);
  }
  LoRa.setSpreadingFactor(7);     // Fastest transmission mode
  LoRa.setSignalBandwidth(250E3); // Widen data pipe to 250kHz

  // Base Station Wi-Fi is an OPEN network for immediate connection
  WiFi.softAP("GroundStation");

  server.on("/", handleRoot);
  server.on("/update", handleUpdate);
  server.begin();
  
  Serial.println("\n[SUCCESS] Ground Station Ready. Hosting Wi-Fi and listening to mesh.");
}

void loop() {
  server.handleClient(); 

  // 1. Watchdog: If phone disconnects or stops sending GPS, revert to 0.0
  if (millis() - lastGpsTime > gpsTimeout) {
    anchorLat = "0.0";
    anchorLng = "0.0";
  }

  // 2. Listen to LoRa Mesh for incoming Fleet Trucks
  int packetSize = LoRa.parsePacket();
  if (packetSize) {
    String incoming = "";
    while (LoRa.available()) {
      char c = (char)LoRa.read();
      if (isPrintable(c)) {
        incoming += c;
      }
    }
    
    // Forward Truck telemetry to Unity via USB Serial immediately (No delays!)
    if (incoming.startsWith("TRUCK_")) {
      Serial.println(incoming); 
    }
  }

  // Broadcast BASE_STATION at 1Hz — matches phone GPS hardware update rate.
  // Previous 200ms rate flooded 433MHz with 5 duplicate packets/sec,
  // causing airwave collisions whenever the truck also tried to transmit.
  if (millis() - lastSerialTime > 1000) {
    lastSerialTime = millis();
    
    String anchorPayload = "BASE_STATION," + anchorLat + "," + anchorLng;
    
    // Stream to Laptop / Unity
    Serial.println(anchorPayload);
    
    // Broadcast over RF to Truck Nodes
    LoRa.beginPacket();
    LoRa.print(anchorPayload);
    LoRa.endPacket();
  }
}
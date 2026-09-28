#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#include <Wire.h>
#include <SPI.h>
#include <LoRa.h>

ESP8266WebServer server(80);

// Hardware Identification
String nodeID = "";

// Sensor Pins
const int trigPin = 0;  // D3
const int echoPin = 16; // D0
const uint8_t MPU_ADDR = 0x68;

// Data Storage
String localLat = "0.0";
String localLng = "0.0";
long distance = 999;

// Watchdog Timer
unsigned long lastGpsTime = 0;
const unsigned long gpsTimeout = 4000;

// IMU Variables & Calibration Offsets
int16_t AcX, AcY, AcZ, Tmp, GyX, GyY, GyZ;
long offAcX = 0, offAcY = 0, offAcZ = 0;
long offGyX = 0, offGyY = 0, offGyZ = 0;

unsigned long lastTxTime = 0;
// 500ms = 2Hz transmission
const unsigned long txInterval = 500; 

// Captive Portal Webpage
const char PAGE_HTML[] PROGMEM = R"=====(
<!DOCTYPE html>
<html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<style>body{font-family: Arial; text-align: center; margin-top: 50px; background-color: #f4f4f9;}</style>
<title>Truck Node Config</title></head><body>
<h2 id="nodeHeader">Truck Node Active</h2>
<p id="status">Acquiring GPS fix...</p>
<h3 id="coords" style="color: green;"></h3>
<script>
  function sendLocation(lat, lng) {
    fetch('/update?lat=' + lat + '&lng=' + lng).catch(e => console.error(e));
  }
  if (navigator.geolocation) {
    navigator.geolocation.watchPosition(function(pos) {
      let lat = pos.coords.latitude.toFixed(6);
      let lng = pos.coords.longitude.toFixed(6);
      document.getElementById("status").innerHTML = "Streaming coordinates to node...";
      document.getElementById("coords").innerHTML = "Lat: " + lat + "<br>Lng: " + lng;
      sendLocation(lat, lng);
    }, function(err) {
      document.getElementById("status").innerHTML = "<span style='color:red'>GPS Error: " + err.message + "</span>";
    }, { enableHighAccuracy: true });
  }
</script>
</body></html>
)=====";

void handleRoot() { 
  server.send(200, "text/html", PAGE_HTML); 
}

void handleUpdate() {
  if (server.hasArg("lat") && server.hasArg("lng")) {
    localLat = server.arg("lat");
    localLng = server.arg("lng");
    lastGpsTime = millis(); // Reset watchdog
    server.send(200, "text/plain", "OK");
  } else {
    server.send(400, "text/plain", "Missing args");
  }
}

// CRITICAL: Truck MUST be completely stationary and flat on the ground when powering on.
void calibrateMPU() {
  const int samples = 100;
  for (int i = 0; i < samples; i++) {
    Wire.beginTransmission(MPU_ADDR);
    Wire.write(0x3B);
    Wire.endTransmission(false);
    
    Wire.requestFrom((uint8_t)MPU_ADDR, (size_t)14, (bool)true);

    offAcX += (int16_t)(Wire.read() << 8 | Wire.read());
    offAcY += (int16_t)(Wire.read() << 8 | Wire.read());
    offAcZ += (int16_t)(Wire.read() << 8 | Wire.read());
    Tmp     = (int16_t)(Wire.read() << 8 | Wire.read()); 
    offGyX += (int16_t)(Wire.read() << 8 | Wire.read());
    offGyY += (int16_t)(Wire.read() << 8 | Wire.read());
    offGyZ += (int16_t)(Wire.read() << 8 | Wire.read());
    delay(10);
  }
  offAcX /= samples;
  offAcY /= samples;
  offAcZ = (offAcZ / samples) - 16384; 
  offGyX /= samples;
  offGyY /= samples;
  offGyZ /= samples;
}

void setupMPU() {
  Wire.begin(4, 5); // SDA = D2, SCL = D1
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(0x6B); 
  Wire.write(0);
  Wire.endTransmission(true);
  calibrateMPU();
}

void readSonar() {
  digitalWrite(trigPin, LOW); 
  delayMicroseconds(2);
  digitalWrite(trigPin, HIGH); 
  delayMicroseconds(10);
  digitalWrite(trigPin, LOW);
  long duration = pulseIn(echoPin, HIGH, 25000); 
  distance = (duration == 0) ? 999 : (duration / 2) / 29.1; 
}

void readIMU() {
  // 6-DOF IMU
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(0x3B); 
  Wire.endTransmission(false);
  Wire.requestFrom((uint8_t)MPU_ADDR, (size_t)14, (bool)true); 
  
  if (Wire.available() == 14) {
    AcX = (int16_t)(Wire.read() << 8 | Wire.read()) - offAcX;
    AcY = (int16_t)(Wire.read() << 8 | Wire.read()) - offAcY;
    AcZ = (int16_t)(Wire.read() << 8 | Wire.read()) - offAcZ;
    Tmp = (int16_t)(Wire.read() << 8 | Wire.read());
    GyX = (int16_t)(Wire.read() << 8 | Wire.read()) - offGyX;
    GyY = (int16_t)(Wire.read() << 8 | Wire.read()) - offGyY;
    GyZ = (int16_t)(Wire.read() << 8 | Wire.read()) - offGyZ;
  }
}

void setup() {
  Serial.begin(115200);
  delay(500);
  
  pinMode(trigPin, OUTPUT);
  pinMode(echoPin, INPUT);
  
  setupMPU();

  nodeID = "TRUCK_" + String(ESP.getChipId(), HEX);
  nodeID.toUpperCase();

  LoRa.setPins(15, 2, -1); 
  if (!LoRa.begin(433E6)) {
    Serial.println("\n[ERROR] LoRa init failed.");
    while (true) delay(1000);
  }
  LoRa.setSpreadingFactor(7);     
  LoRa.setSignalBandwidth(250E3); 

  WiFi.mode(WIFI_AP);
  WiFi.softAP(nodeID.c_str(), "12345678");

  server.on("/", handleRoot);
  server.on("/update", handleUpdate);
  server.begin();
}

void loop() {
  server.handleClient(); 

  // Watchdog timeout check
  if (millis() - lastGpsTime > gpsTimeout) {
    localLat = "0.0";
    localLng = "0.0";
  }

  // 1. Ingest telemetry over LoRa mesh
  int packetSize = LoRa.parsePacket();
  if (packetSize > 0 && packetSize < 128) {
    String incoming = "";
    while (LoRa.available()) {
      char c = (char)LoRa.read();
      if (isPrintable(c)) {
        incoming += c;
      }
    }
    
    if ((incoming.startsWith("TRUCK_") || incoming.startsWith("BASE_STATION")) 
        && !incoming.startsWith(nodeID) 
        && incoming.indexOf(',') != -1) {
      Serial.println(incoming);
    }
  }

  // 2. Sample IMU data
  readIMU();

  // 3. Broadcast local telemetry
  if (millis() - lastTxTime > txInterval) {
    lastTxTime = millis();
    readSonar(); 
    
    String coreData = localLat + "," + localLng + "," + 
                      String(distance) + "," + 
                      String(AcX) + "," + String(AcY) + "," + String(AcZ) + "," + 
                      String(GyX) + "," + String(GyY) + "," + String(GyZ);

    LoRa.beginPacket();
    LoRa.print(nodeID + "," + coreData);
    LoRa.endPacket();

    Serial.println("SELF," + coreData);
  }
}
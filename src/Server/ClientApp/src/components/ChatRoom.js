import React, { Component } from 'react';

export class ChatRoom extends Component {
  static displayName = ChatRoom.name;

  constructor(props) {
    super(props);
    const { id } = this.props.match.params;
    this.state = {
      room: id,
      ws: null,
      message: '',
      buffer: '',
    };

    this.sendMessage = this.sendMessage.bind(this);
    this.handleChange = this.handleChange.bind(this);
  }

  componentDidMount() {
    console.log('component did mount')
    this.connect();
  }

  connect() {
    console.log('initializing web socket connection')
    var ws = new WebSocket('ws://' + location.host + '/api/rooms/' + this.state.room);
    let self = this;
    ws.onopen = () => {
      console.log('room id ' + self.state.room + ' connected')
      self.setState({ ws: ws });
    }
    
    ws.onclose = e => {
      console.log('ws closed: ' + e.reason);
      setTimeout(self.reconnect, 0);
    }

    ws.onerror = err => {
      console.error('ws error: ', err.message);
      ws.close();

      setTimeout(self.reconnect, 0);
    }

    ws.onmessage = e => {
      let newBuffer = this.state.buffer + '\n' + this.state.message;
      self.setState({
        buffer: newBuffer,
      })
    }
  }

  reconnect = () => {
    const { ws } = this.state;
    if (!ws || ws.readyState == WebSocket.CLOSED) {
      this.connect();
    }
  }

  sendMessage(event) {
    this.state.ws.send(this.state.message);
    let newBuffer = this.state.buffer + '\n' + this.state.message;
    this.setState({
      message: '',
      buffer: newBuffer
    });
    event.preventDefault();
  }

  handleChange(event) {
    this.setState({message: event.target.value});
  }

  render() {
    return (
      <div>
        <h1>{this.state.id}</h1>

        <textarea readOnly={true} value={this.state.buffer} />

        <input type="text" value={this.state.message} onChange={this.handleChange} />

        <button className="btn btn-primary" onClick={this.sendMessage}>Send</button>
      </div>
    );
  }
}

import React, { Component } from 'react';
import { Link } from 'react-router-dom'; 

export class RoomMenu extends Component {
  static displayName = RoomMenu.name;

  constructor(props) {
    super(props);
    this.state = { rooms: [], loading: true };
  }

  componentDidMount() {
    this.populateRoomData();
  }

  static renderRoomsList(r) {
    return (
      <ul>
          {r.map(room =>
            <li key={room.id}><Link to={'/rooms/'+ room.id} replace>{room.name}</Link></li>)
          }
      </ul>
    );
  }

  render() {
    let contents = this.state.loading
      ? <p><em>Loading...</em></p>
      : RoomMenu.renderRoomsList(this.state.rooms);

    return (
      <div>
        <h1 id="tabelLabel" >Rooms</h1>
        <p>Plase select a chatroom from the list of options.</p>
        {contents}
      </div>
    );
  }

  async populateRoomData() {
    const response = await fetch('/api/rooms');
    const data = await response.json();
    this.setState({ rooms: data, loading: false });
  }
}